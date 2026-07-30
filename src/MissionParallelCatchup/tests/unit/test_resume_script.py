"""The worker's resume decision, run as the shell script it actually is.

Measured on ssc-test 2026-07-30: a1 replayed range 16752063 to its target
ledger and was evicted before it could exit 0. a2 resumed, found LCL == TARGET,
ran catchup against a DB with nothing left to apply, and stellar-core exited 2
-- deterministically, every attempt. The range exhausted its budget and the
mission aborted a 61%-complete 2096-worker run over work that had actually been
done.
"""

import os
import re
import subprocess

import pytest

import job_monitor as jm


TARGET = 16752063
COUNT = 16320

# What `stellar-core offline-info --console` really prints: bucketlist puts ~40
# lines of hashes between the "ledger": key and the "num" the probe wants, which
# is why the probe must not window its grep. Verified against 27.1.1 on ssc-test
# 2026-07-30 -- exactly one "num" key in the document, and it is the ledger's.
def offline_info(lcl):
    buckets = ',\n'.join(f'            "{i:064x}"' for i in range(40))
    return ('{\n  "info" : {\n    "ledger" : {\n'
            f'      "age" : 3,\n      "closeTime" : 1753000000,\n'
            f'      "hash" : "abc",\n'
            f'      "bucketListHashes" : [\n{buckets}\n      ],\n'
            f'      "num" : {lcl},\n      "version" : 22\n'
            '    }\n  }\n}')


@pytest.fixture
def run_resume(tmp_path):
    """Run RESUME_SCRIPT against a stubbed stellar-core on a private /data."""
    def run(lcl, mark_matches=True, prev_log_lcl=None):
        data = tmp_path / 'data'
        data.mkdir(exist_ok=True)
        bindir = tmp_path / 'bin'
        bindir.mkdir(exist_ok=True)
        stub = bindir / 'stellar-core'
        info = offline_info(lcl).replace("'", "") if lcl is not None else ''
        stub.write_text(
            '#!/bin/sh\n'
            'for a in "$@"; do case "$a" in\n'
            "  offline-info) " +
            (f"cat <<'EOF'\n{info}\nEOF\n" if lcl is not None else 'echo "{}"; ') +
            '    exit 0;;\n'
            '  new-db)  echo "RAN:new-db"  >> "$STUBLOG"; exit 0;;\n'
            '  catchup) echo "RAN:catchup" >> "$STUBLOG"; exit 2;;\n'
            'esac; done\nexit 0\n')
        stub.chmod(0o755)

        src = jm.RESUME_SCRIPT % {'key': f"{TARGET}/{COUNT}",
                                  'target': TARGET, 'count': COUNT}
        src = src.replace('/usr/bin/stellar-core', str(stub))
        src = src.replace('/data/', str(data) + '/')

        if mark_matches:
            (data / '.job-key').write_text(f"{TARGET}/{COUNT}")
        if prev_log_lcl is not None:
            (data / 'stellar-core.log').write_text(
                f"Ledger close complete: {prev_log_lcl}\n")

        stublog = tmp_path / 'stub.log'
        if stublog.exists():
            stublog.unlink()
        env = dict(os.environ, STUBLOG=str(stublog))
        r = subprocess.run(['/bin/sh', '-c', src], capture_output=True, text=True,
                           env=env, timeout=30)
        ran = stublog.read_text().split() if stublog.exists() else []
        return r.returncode, r.stdout, ran
    return run


def test_a_range_already_at_its_target_exits_success_without_recatching(run_resume):
    code, out, ran = run_resume(lcl=TARGET)
    assert 'ALREADY COMPLETE' in out, out
    assert code == 0, f"exit {code}; a finished range must not fail"
    assert 'RAN:catchup' not in ran, "re-ran catchup on a completed range -> exit 2"
    assert 'RAN:new-db' not in ran, "wiped a completed range"


def test_a_partially_replayed_range_still_resumes(run_resume):
    code, out, ran = run_resume(lcl=TARGET - 100)
    assert 'RESUME:' in out and 'ALREADY COMPLETE' not in out, out
    assert 'RAN:catchup' in ran and 'RAN:new-db' not in ran, ran


def test_a_range_that_never_started_replay_starts_fresh(run_resume):
    code, out, ran = run_resume(lcl=None)
    assert 'RESUME DECLINED' in out, out
    assert 'RAN:new-db' in ran and 'RAN:catchup' in ran, ran


def test_a_range_whose_replay_never_reached_its_own_span_starts_fresh(run_resume):
    # Bucket apply uses createWithoutLoading() -- an unconditional INSERT that
    # assumes a fresh DB -- so a crash before replay must start over. An LCL
    # below TARGET-COUNT means the bucket phase, not replay.
    code, out, ran = run_resume(lcl=TARGET - COUNT - 1)
    assert 'RESUME DECLINED' in out, out
    assert 'RAN:new-db' in ran


def test_the_lcl_probe_reads_past_the_bucketlist(run_resume):
    # offline-info puts ~40 lines of bucketlist hashes between "ledger": and
    # "num", so `grep -A8 '"ledger":'` yields nothing and the probe degrades to
    # the log fallback silently -- shipped exactly that once.
    code, out, ran = run_resume(lcl=TARGET - 100)
    assert f"RESUME PROBE: offline-info reports lcl {TARGET - 100}" in out, out


def test_the_log_fallback_covers_a_core_that_answers_nothing(run_resume):
    # Goes blind above INFO, which is why it is no longer the primary probe --
    # but a core that cannot answer offline-info still leaves its own log.
    code, out, ran = run_resume(lcl=None, prev_log_lcl=TARGET - 50)
    assert 'RESUME:' in out, out
    assert 'RAN:new-db' not in ran


def test_a_volume_left_by_a_different_range_is_never_resumed_from(run_resume):
    # /data is per-range, but a recycled volume or a mis-scheduled pod would
    # otherwise resume a DB belonging to some other span.
    code, out, ran = run_resume(lcl=TARGET - 100, mark_matches=False)
    assert 'RESUME' not in out, out
    assert 'RAN:new-db' in ran and 'RAN:catchup' in ran


def test_the_resume_script_survives_its_own_percent_formatting():
    # RESUME_SCRIPT is %-formatted with the range's key/target/count at dispatch.
    # A bare % anywhere in it -- including in a comment -- raises at runtime and
    # takes down every job dispatch. Nearly shipped exactly that: a comment
    # reading "61%-complete".
    jm.RESUME_SCRIPT % {'key': '123/456', 'target': 123, 'count': 456}  # must not raise
    # %% is a legitimate escape (printf '%%s'), so strip those pairs before
    # looking for a stray one.
    probe = jm.RESUME_SCRIPT.replace('%%', '')
    stray = [m.start() for m in re.finditer(r"%(?!\()", probe)]
    assert not stray, f"bare % near {probe[max(0, stray[0] - 40):stray[0] + 20]!r}"
