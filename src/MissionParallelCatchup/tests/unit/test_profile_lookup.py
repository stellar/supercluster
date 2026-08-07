"""Loading a previous run's measurements, and picking the entry to size from.

A profile is an optimisation, never a prerequisite: absent, unreadable and
malformed all have to mean "use the configured defaults".
"""

import json

import pytest

import config
import profiles
import job_monitor as jm


PROFILE_RANGES = [
    (1000, {'peakAnonBytes': 1_000_000_000, 'peakWorkingSetBytes': 9_000_000_000,
            'peakEphemeralBytes': 2_000_000_000}),
    (2000, {'peakAnonBytes': 3_000_000_000, 'peakWorkingSetBytes': 13_000_000_000,
            'peakEphemeralBytes': 4_000_000_000}),
]


@pytest.fixture
def profile(monkeypatch):
    """Install a loaded profile, as load_profile() would have left it."""
    def install(ranges=PROFILE_RANGES):
        monkeypatch.setattr(config, 'PROFILE', sorted(ranges))
    return install


@pytest.fixture
def written(tmp_path, monkeypatch):
    """Write a profile document and load it through the real reader."""
    def load(doc, mode='ephemeral', text=None):
        path = tmp_path / 'profile.json'
        path.write_text(text if text is not None else json.dumps(doc))
        monkeypatch.setattr(config, 'PROFILE_PATH', str(path))
        monkeypatch.setattr(config, 'STORAGE_MODE', mode)
        return profiles.load_profile()
    return load


# --- picking an entry --------------------------------------------------------

def test_profile_prefers_an_exact_end(profile):
    profile()
    assert profiles.profile_for(2000)['peakAnonBytes'] == 3_000_000_000


def test_profile_rounds_up_to_the_next_measured_end_never_down(profile):
    # Cost rises with ledger position -- the bucket set only grows -- so a lower
    # neighbour under-reports, and under-provisioning costs an eviction while
    # over-provisioning only costs packing density.
    profile()
    assert profiles.profile_for(1500)['peakAnonBytes'] == 3_000_000_000, \
        "1500 must size from 2000, not from 1000"


def test_profile_falls_back_to_defaults_past_its_high_water_mark(profile):
    # An older profile has nothing above its own top, which is exactly where a
    # newer run's fresh ranges live. Extrapolating there would under-provision.
    profile()
    assert profiles.profile_for(9999) is None


def test_no_profile_at_all_is_not_an_error(monkeypatch):
    monkeypatch.setattr(config, 'PROFILE', None)
    assert profiles.profile_for(1000) is None
    monkeypatch.setattr(config, 'PROFILE', [])
    assert profiles.profile_for(1000) is None


# --- reading the document ----------------------------------------------------

def test_no_configured_path_means_no_profile(monkeypatch):
    monkeypatch.setattr(config, 'PROFILE_PATH', '')
    assert profiles.load_profile() == []


def test_an_unreadable_profile_is_not_fatal(written, tmp_path, monkeypatch):
    # It is an optimisation, never a prerequisite.
    assert written(None, text='{not json') == []
    monkeypatch.setattr(config, 'PROFILE_PATH', str(tmp_path / 'nope.json'))
    assert profiles.load_profile() == []


def test_a_matching_profile_keeps_every_axis(written):
    got = written({'storageMode': 'ephemeral',
                   'ranges': {'2000': PROFILE_RANGES[1][1]}})
    assert got == [(2000, PROFILE_RANGES[1][1])]


def test_entries_come_back_sorted_by_range_end(written):
    # profile_for() bisects the list, so an unsorted load would silently size
    # ranges from the wrong neighbour.
    got = written({'storageMode': 'ephemeral',
                   'ranges': {'3000': {}, '1000': {}, '2000': {}}})
    assert [end for end, _ in got] == [1000, 2000, 3000]


def test_a_non_numeric_range_key_is_skipped_not_fatal(written):
    got = written({'storageMode': 'ephemeral',
                   'ranges': {'2000': {'peakAnonBytes': 1}, 'tip': {'peakAnonBytes': 2}}})
    assert [end for end, _ in got] == [2000]


def test_a_cross_mode_profile_keeps_memory_but_drops_disk(written):
    # cpu and memory measure the same work in either mode. Disk does not: a pvc
    # run never measures node-local usage at all, so its absence must fall back
    # to the configured default rather than size the wrong dimension. Degrade,
    # never reject -- a rejected profile loses the transferable axes too.
    got = written({'storageMode': 'pvc', 'ranges': {'2000': PROFILE_RANGES[1][1]}},
                  mode='ephemeral')
    assert len(got) == 1
    rec = got[0][1]
    assert 'peakEphemeralBytes' not in rec
    assert rec['peakAnonBytes'] == 3_000_000_000


def test_a_profile_with_no_declared_mode_is_taken_at_face_value(written):
    # Pre-dates the field; rejecting it would discard every older artifact.
    got = written({'ranges': {'2000': PROFILE_RANGES[1][1]}}, mode='ephemeral')
    assert got[0][1]['peakEphemeralBytes'] == 4_000_000_000
