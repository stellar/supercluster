"""Test harness for job_monitor: a fake cluster wired into the real module.

`cluster` is the fixture. It replaces job_monitor's module-level API clients
with fake_k8s, points every path the monitor writes at tmp_path, and hands back
a driver that runs the real reconcile() -- no source extraction, no mirrors.

    def test_something(cluster):
        cluster.reconcile()                       # one real reconcile pass
        cluster.advance(300, 'succeeded')         # move a range to a named state
        cluster.reconcile()
        assert '300' in cluster.progress()['completed']

Nothing here weakens the monitor: the only production change this needed was
making import side-effect free (log dir fallback, in-cluster config guarded on
KUBERNETES_SERVICE_HOST). Every decision under test is the shipped code path.
"""

import json
import os

import pytest

import fake_k8s
import job_monitor as jm

# Config the fixture pins. Small on purpose: three ranges and PARALLELISM 2 so
# dispatch capacity, retry and completion are all observable in a few passes.
DEFAULT_CONFIG = {
    'NAMESPACE': 'catchup-test',
    'RUN_NAME': 'pc',
    'CORE_IMAGE': 'stellar/stellar-core:test',
    'STARTING_LEDGER': 0,
    'LATEST_LEDGER_NUM': 300,
    'LEDGERS_PER_JOB': 100,
    'OVERLAP_LEDGERS': 320,
    'RANGE_GENERATOR': 'uniform',
    'RANGE_ORDER': 'tip-first',
    'PARALLELISM': 2,
    'STORAGE_MODE': 'pvc',
    'STORAGE_SIZE': '40Gi',
    'STORAGE_CLASS': 'gp3',
    'SAVE_SUCCESS_LOGS': True,
    'PROFILE_PATH': '',
    'ATTEMPT_DEADLINE_SECONDS': 0,
    'MAX_ATTEMPTS_PER_RANGE': 5,
    'MAX_DISRUPTION_ATTEMPTS': 20,
    'MAX_EPHEMERAL_ATTEMPTS': 4,
    'LIM_EPHEMERAL': '',
    'REQ_EPHEMERAL': '',
}

# What advance() does to the fake cluster for each name. The verdict the monitor
# then reaches is its own business -- that is the thing under test.
STATES = (
    'pending',      # dispatched, nothing scheduled yet
    'running',      # pod Running, job active
    'succeeded',    # exit 0
    'incomplete',   # exit 3: did-not-complete, retryable on the range budget
    'condemned',    # exit 1: genuine catchup failure, no retry
    'oom',          # exit 137 / OOMKilled
    'disrupted',    # DisruptionTarget condition -- spot eviction
    'ephemeral',    # kubelet eviction for exceeding the ephemeral-storage limit
    'rejected',     # kubelet refused the pod before any container ran
    'timeout',      # activeDeadlineSeconds fired
    'unknown',      # job failed, pod already reaped, nothing classified it
    'no_exit_code', # container terminated, kubelet never filled in the exit code
)


class Driver:
    """Runs reconcile passes against the fake cluster and inspects the results."""

    def __init__(self, k8s, tmp_path, config):
        self.k8s = k8s
        self.jm = jm
        self.tmp_path = tmp_path
        self.config = config
        self.namespace = config['NAMESPACE']
        self.run_name = config['RUN_NAME']
        self.log_dir = jm.LOG_DIR
        # Same dict update_status_and_metrics() carries across iterations of the
        # loop, so multi-pass tests see the real cross-pass behaviour (halt on
        # regression, histogram replay guard, counter deltas).
        self.state = {'owner': None, 'replayed': set(), 'max_completed': 0,
                      'halted': False, 'counted': {}}
        self.results = []

    # -- driving -------------------------------------------------------------

    def reconcile(self):
        """One real reconcile() pass. Returns the summary dict it produces."""
        if self.state['owner'] is None:
            self.state['owner'] = jm.owner_ref()
            jm._progress_owner['ref'] = self.state['owner']
        result = jm.reconcile(self.state)
        self.results.append(result)
        return result

    def advance(self, end, state, attempt=None):
        """Move a range's Job/Pod to a named state, as the cluster would.

        `end` is the range end (int or str); attempt defaults to the newest Job
        this range has.
        """
        if state not in STATES:
            raise ValueError(f"unknown state {state!r}; expected one of {STATES}")
        name = self.job_name(end, attempt)
        pod = self.k8s.pod_for_job(name)
        pod_name = pod.metadata.name if pod is not None else None

        if state == 'pending':
            return name
        if state == 'running':
            self.k8s.set_job_running(name)
            return name
        if state == 'succeeded':
            if pod_name:
                self.k8s.set_pod_terminated(pod_name, exit_code=0)
            self.k8s.set_job_succeeded(name)
            return name

        # Everything below is a failure; the Job condition and the pod detail
        # are set independently because in a real run either can be missing.
        if state == 'incomplete':
            if pod_name:
                self.k8s.set_pod_terminated(pod_name, exit_code=3)
            self.k8s.set_job_failed(name, message=self._policy_msg(pod_name, 3, 2))
        elif state == 'condemned':
            if pod_name:
                self.k8s.set_pod_terminated(pod_name, exit_code=1)
            self.k8s.set_job_failed(name, message=self._policy_msg(pod_name, 1, 2))
        elif state == 'oom':
            if pod_name:
                self.k8s.set_pod_terminated(pod_name, exit_code=137, reason='OOMKilled')
            self.k8s.set_job_failed(name, message=self._policy_msg(pod_name, 137, 1))
        elif state == 'disrupted':
            if pod_name:
                self.k8s.set_pod_condition(pod_name, 'DisruptionTarget',
                                           reason='TerminationByKubelet')
                self.k8s.set_pod_terminated(pod_name, exit_code=3)
            self.k8s.set_job_failed(name, message=self._policy_msg(pod_name, None, 0))
        elif state == 'ephemeral':
            if pod_name:
                # stellar-core drains on the eviction SIGTERM and exits 3, so the
                # exit code alone is indistinguishable from a catchup failure;
                # status.message is the only discriminator.
                self.k8s.set_pod_terminated(pod_name, exit_code=3, phase='Failed')
                self.k8s.set_pod_phase(
                    pod_name, 'Failed', reason='Evicted',
                    message=('Pod ephemeral local storage usage exceeds the total '
                             'limit of containers 40Gi'))
            self.k8s.set_job_failed(name, message=self._policy_msg(pod_name, 3, 2))
        elif state == 'rejected':
            if pod_name:
                self.k8s.set_pod_phase(pod_name, 'Failed',
                                       reason='VolumeAttachmentLimitExceeded',
                                       message='Node has reached its volume '
                                               'attachment limit, rejecting pod')
            self.k8s.set_job_failed(name, reason='BackoffLimitExceeded',
                                    message='Job has reached the specified backoff limit')
        elif state == 'timeout':
            if pod_name:
                self.k8s.set_pod_terminated(pod_name, exit_code=3)
            self.k8s.set_job_failed(name, reason='DeadlineExceeded',
                                    message='Job was active longer than specified deadline')
        elif state == 'unknown':
            if pod_name:
                self.k8s.delete_pod(pod_name)
            self.k8s.set_job_failed(name, reason=None)
        elif state == 'no_exit_code':
            # The container terminated but the kubelet never populated an exit
            # code, so nothing on the pod says why it stopped. Real: observed on
            # range 59018943, 2026-07-30.
            if pod_name:
                # The only terminated status left on the pod belongs to the
                # sidecar, which exited cleanly; stellar-core's never landed. So
                # classify() finds a terminated container, none of them non-zero,
                # and falls off the end of its loop.
                self.k8s.set_pod_terminated(pod_name, exit_code=0,
                                            container='log-collector',
                                            phase='Failed')
            self.k8s.set_job_failed(name, reason='BackoffLimitExceeded',
                                    message='Job has reached the specified backoff limit')
        return name

    def _policy_msg(self, pod_name, code, rule_index):
        """A podFailurePolicy failure message in the Job controller's own format."""
        if code is None:
            return (f"Container stellar-core for pod {self.namespace}/{pod_name} "
                    f"matching FailJob rule at index {rule_index}")
        return (f"Container stellar-core for pod {self.namespace}/{pod_name} failed "
                f"with exit code {code} matching FailJob rule at index {rule_index}")

    # -- the collector's side of the contract --------------------------------

    def finalize(self, end, attempt=1, tx_apply=None, peaks=None, resumed=False,
                 attempt_seconds=None):
        """Write what the log-collector sidecar writes for a finished attempt.

        The monitor will not reap a Job until the .done marker exists, and reads
        peaks and txApply out of .metrics -- so a test that wants either of those
        paths has to stand in for the collector.
        """
        data = dict(peaks or {})
        if tx_apply is not None:
            data['txApplySeconds'] = tx_apply
        if attempt_seconds is not None:
            data['attemptSeconds'] = attempt_seconds
        if resumed:
            data['resumed'] = True
        self.write(jm.metrics_path(str(end), attempt), json.dumps(data))
        self.write(jm.done_path(str(end), attempt), '')

    def write(self, path, text):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w') as fh:
            fh.write(text)
        return path

    # -- inspection ----------------------------------------------------------

    def job_name(self, end, attempt=None):
        if attempt is not None:
            return jm.job_name(int(end), attempt)
        prefix = f"{self.run_name}-r{int(end)}-a"
        names = [n for n in self.k8s.job_names(self.namespace) if n.startswith(prefix)]
        if not names:
            raise AssertionError(f"no Job for range {end}; have {self.k8s.job_names()}")
        return max(names, key=lambda n: int(n.rsplit('-a', 1)[1]))

    def attempt_of(self, end):
        return int(self.job_name(end).rsplit('-a', 1)[1])

    def jobs(self):
        return self.k8s.job_names(self.namespace)

    def pvcs(self):
        return self.k8s.pvc_names(self.namespace)

    def progress(self):
        """The authoritative progress record, straight off disk."""
        try:
            with open(jm.PROGRESS_FILE) as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {}

    def progress_configmap(self):
        """The best-effort ConfigMap mirror the mission driver reads."""
        data = self.k8s.config_map_data(jm.PROGRESS_CM, self.namespace) or {}
        return json.loads(data.get('progress.json', '{}'))

    def completed(self):
        return self.progress().get('completed', {})

    def failed(self):
        return self.progress().get('failed', {})

    @property
    def calls(self):
        return self.k8s.calls

    @property
    def deleted(self):
        return self.k8s.deleted


@pytest.fixture
def cluster(tmp_path, monkeypatch):
    config = dict(DEFAULT_CONFIG)
    log_dir = tmp_path / 'logs'
    log_dir.mkdir()

    k8s = fake_k8s.FakeCluster(namespace=config['NAMESPACE'])
    monkeypatch.setattr(jm, 'core_v1', k8s.core_v1)
    monkeypatch.setattr(jm, 'batch_v1', k8s.batch_v1)

    for key, value in config.items():
        monkeypatch.setattr(jm, key, value)
    # Derived at import from RUN_NAME / LOG_DIR, so they have to follow.
    monkeypatch.setattr(jm, 'LOG_DIR', str(log_dir))
    monkeypatch.setattr(jm, 'PROGRESS_FILE', str(log_dir / 'progress.json'))
    monkeypatch.setattr(jm, 'PROGRESS_CM', f"{config['RUN_NAME']}-catchup-progress")
    # Module-level mutable state that would otherwise leak between tests.
    monkeypatch.setattr(jm, 'PROFILE', None)
    monkeypatch.setattr(jm, '_progress_owner', {})

    # The chart's ConfigMap: owner_ref() reads it, and every Job, PVC and the
    # progress ConfigMap hang off it.
    k8s.add_config_map(f"{config['RUN_NAME']}-stellar-core-config",
                       {'stellar-core.cfg': '# test'})

    return Driver(k8s, tmp_path, config)
