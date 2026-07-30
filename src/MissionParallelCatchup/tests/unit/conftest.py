"""Fixtures for the imported-function unit tests.

These tests call job_monitor / log_collector functions directly. Almost all of
them touch the shared logs volume through LOG_DIR-derived paths, so the one
thing they all need is that directory pointed somewhere disposable -- and
pointed at the SAME place in both modules, which is the contract the two
processes actually run under.
"""

import pytest

import job_monitor as jm
import log_collector as lc


@pytest.fixture
def logdir(tmp_path, monkeypatch):
    """The shared volume, as both processes see it."""
    d = tmp_path / 'logs'
    d.mkdir()
    monkeypatch.setattr(jm, 'LOG_DIR', str(d))
    monkeypatch.setattr(lc, 'LOG_DIR', str(d))
    return d
