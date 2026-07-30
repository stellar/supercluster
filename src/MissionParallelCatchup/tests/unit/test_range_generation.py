"""The ledger range list, and the order it is dispatched in.

generate_ranges() must stay a pure function of config: dispatch derives the
full list on every reconcile, so a restart has to reproduce it exactly.
"""

import pytest

import job_monitor as jm


@pytest.fixture
def ranges(monkeypatch):
    """Configure the generator and return a callable that runs it."""
    def configure(generator='uniform', order='tip-first', parallelism=4,
                  start=39990000, latest=40000000, per_job=1000,
                  floor=64000, overlap=320):
        monkeypatch.setattr(jm, 'RANGE_GENERATOR', generator)
        monkeypatch.setattr(jm, 'RANGE_ORDER', order)
        monkeypatch.setattr(jm, 'PARALLELISM', parallelism)
        monkeypatch.setattr(jm, 'STARTING_LEDGER', start)
        monkeypatch.setattr(jm, 'LATEST_LEDGER_NUM', latest)
        monkeypatch.setattr(jm, 'LEDGERS_PER_JOB', per_job)
        monkeypatch.setattr(jm, 'LOGARITHMIC_FLOOR_LEDGERS', floor)
        monkeypatch.setattr(jm, 'OVERLAP_LEDGERS', overlap)
        return jm.generate_ranges()
    return configure


def test_generators_emit_tip_first_by_default(ranges):
    r = ranges()
    assert r[0][0] > r[-1][0], "index 0 must be the tip"


def test_oldest_first_reverses_dispatch_without_dropping_ranges(ranges):
    # A profiling run wants the cheap early ranges measured first: the bucket
    # set only grows with ledger position, so tip-first front-loads the
    # expensive ones and an interrupted run profiles nothing cheap.
    tip = ranges(order='tip-first')
    old = ranges(order='oldest-first')
    assert old == list(reversed(tip))
    assert sorted(old) == sorted(tip), "reversing must not change the range set"


def test_every_range_carries_the_overlap_on_top_of_its_ledger_count(ranges):
    # The count is what the worker is asked to catch up, and it is always the
    # segment plus OVERLAP_LEDGERS -- measuring with overlap 0 measures nothing
    # the run will ever dispatch.
    r = ranges(per_job=1000, overlap=320)
    assert {count for _, count in r} == {1320}


def test_the_ranges_tile_the_ledger_space_with_no_gap(ranges):
    r = sorted(ranges(start=0, latest=10000, per_job=1000, overlap=320))
    ends = [end for end, _ in r]
    assert ends == list(range(1000, 10001, 1000))
    assert ends[-1] == 10000, "the tip must be covered"


def test_a_short_tail_segment_is_not_padded_past_the_start(ranges):
    # The last segment is min(remaining, seg_size), so a range list over a span
    # that does not divide evenly must not reach below STARTING_LEDGER.
    r = ranges(start=0, latest=2500, per_job=1000, overlap=0)
    assert sorted(r) == [(500, 500), (1500, 1000), (2500, 1000)]


def test_logarithmic_ranges_match_the_shell_generator(ranges):
    # Verbatim output of logarithmic_range_generator.sh with
    # floor=16000 overlap=320 start=0 latest=500000 parallelism=4, captured
    # before it was deleted. Chunk size halves toward the tip, so exact values
    # are pinned rather than a count.
    expected = ("250000/62820 187500/62820 125000/62820 62500/62820 "
                "375001/31570 343751/31570 312501/31570 281251/31570 "
                "500000/16320 484000/16320 468000/16320 452000/14817").split()
    r = ranges(generator='logarithmic', floor=16000, overlap=320,
               start=0, latest=500000, parallelism=4)
    assert [f"{end}/{count}" for end, count in r] == expected


def test_the_logarithmic_generator_also_honours_dispatch_order(ranges):
    tip = ranges(generator='logarithmic', floor=16000, start=0, latest=500000)
    old = ranges(generator='logarithmic', floor=16000, start=0, latest=500000,
                 order='oldest-first')
    assert old == list(reversed(tip))
