"""What the worker prints, against the collector that reads it off the stream.

RESUME_SCRIPT is the worker's entrypoint and it announces its own decision on
stdout. The collector -- a different process, in a different container, that
never sees the Job spec -- recovers that decision by scanning the log stream for
a marker. That marker is the only way anything downstream knows whether an
attempt did the whole range or only its tail, and the difference matters: a
resumed attempt skips the archive download and the bucket apply, which is where
peak memory happens, so profiling it alone under-reports the range by the whole
download-vs-replay gap. On spot, where eviction is routine and resume is the
entire point of durable /data, that would make a run unprofileable.

The script is executed here rather than quoted: what the collector has to cope
with is the bytes a real /bin/sh emits, not the string literal in job_monitor.

(The resume DECISION -- when to skip new-db, when a range is already complete --
is exercised in tests/unit/test_resume_script.py. This file only pins the
handshake between the two processes.)
"""

import os
import re
import subprocess
import tempfile

import job_monitor as jm
import log_collector as lc

TARGET = 16752063
COUNT = 16320


def _offline_info(lcl):
    """`stellar-core offline-info --console`, as 27.1.1 prints it.

    Whole document on purpose: the probe has to reach "num" past the ~40 lines
    of bucketlist hashes that sit between it and the "ledger" key.
    """
    if lcl is None:
        return '{}'
    hashes = "\n".join(f'            "{i:064x}",' for i in range(40))
    return ('{\n   "info" : {\n      "ledger" : {\n'
            '         "age" : 3,\n'
            f'         "bucketList" : [\n{hashes}\n         ],\n'
            f'         "num" : {lcl},\n         "version" : 23\n'
            '      }\n   }\n}')


def worker_stdout(lcl):
    """Run RESUME_SCRIPT with a stubbed stellar-core; return what it printed."""
    script = jm.RESUME_SCRIPT % {'key': f"{TARGET}/{COUNT}", 'target': TARGET,
                                 'count': COUNT}
    d = tempfile.mkdtemp()
    data = os.path.join(d, 'data')
    os.makedirs(data)
    stub = os.path.join(d, 'stellar-core')
    with open(stub, 'w') as fh:
        fh.write('#!/bin/sh\n'
                 'for a in "$@"; do case "$a" in\n'
                 '  offline-info) cat "$INFO"; exit 0;;\n'
                 '  new-db) exit 0;;\n'
                 '  catchup) exit 0;;\n'
                 'esac; done\nexit 0\n')
    os.chmod(stub, 0o755)
    info = os.path.join(d, 'info.json')
    with open(info, 'w') as fh:
        fh.write(_offline_info(lcl))
    with open(os.path.join(data, '.job-key'), 'w') as fh:
        fh.write(f"{TARGET}/{COUNT}")

    script = script.replace('/usr/bin/stellar-core', stub).replace('/data/', data + '/')
    r = subprocess.run(['/bin/sh', '-c', script], capture_output=True, text=True,
                       env=dict(os.environ, INFO=info), timeout=30)
    return r.stdout


def scan(output):
    scanner = lc.TxApplyScanner()
    for line in output.splitlines():
        scanner.feed(line)
    return scanner


def test_the_collector_sees_a_resume_the_worker_announced():
    out = worker_stdout(lcl=TARGET - 100)
    assert 'RESUME:' in out, out
    assert scan(out).resumed is True, f"the collector missed the marker in:\n{out}"


def test_a_declined_resume_is_not_read_as_a_resume():
    """"RESUME DECLINED" means the opposite and shares a prefix with "RESUME:".

    Reading it as a resume chains a fresh attempt onto the attempts before it
    and maxes their peaks together, inflating every range that ever restarted.
    The colon is what separates them, so it is load-bearing on both sides.
    """
    out = worker_stdout(lcl=None)
    assert 'RESUME DECLINED' in out, out
    assert scan(out).resumed is False, "a declined resume was read as a resume"


def test_the_probe_line_is_not_mistaken_for_the_decision():
    """The script also prints "RESUME PROBE: ..." before it has decided anything.

    It reports the LCL it read, on every attempt including a fresh one, so a
    marker loose enough to match it would mark every attempt resumed.
    """
    out = worker_stdout(lcl=None)
    assert 'RESUME PROBE:' in out
    assert scan("RESUME PROBE: offline-info reports lcl 42").resumed is False


def test_a_range_that_was_already_complete_announces_no_resume():
    """It ran no catchup at all, so there is no measurement to chain."""
    out = worker_stdout(lcl=TARGET)
    assert 'ALREADY COMPLETE' in out, out
    assert scan(out).resumed is False


def test_the_marker_the_collector_greps_is_the_one_the_script_prints():
    """Stated directly, so a rename on either side fails here and not in a run.

    Everything above would still pass if BOTH sides were renamed together --
    which is fine -- but this catches the case where the script's wording drifts
    while the constant does not.
    """
    assert lc.TxApplyScanner.RESUME_MARK in jm.RESUME_SCRIPT, (
        f"the collector greps {lc.TxApplyScanner.RESUME_MARK!r}, which the script "
        "never prints")
    # ...and the decline must not contain it, or the two are indistinguishable.
    decline = re.search(r'echo "(RESUME DECLINED[^"]*)"', jm.RESUME_SCRIPT)
    assert decline, "the script no longer announces a declined resume"
    assert lc.TxApplyScanner.RESUME_MARK not in decline.group(1)
