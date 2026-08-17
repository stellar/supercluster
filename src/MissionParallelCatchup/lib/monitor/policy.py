"""Whether a failed attempt runs again, and with what.

Two questions kept apart:

    may it retry?   a per-CAUSE budget, spent against what actually killed it
    with what?      escalate the axis that ran out, once per cause

A pure function of (verdict, this range's past verdicts), which is what makes
the retry ladder testable without a cluster.
"""
import collections

import config
import sizing

Decision = collections.namedtuple('Decision', 'action reason memory ephemeral')

RETRY, CONDEMN, DEFER = 'retry', 'condemn', 'defer'
WAIT = Decision(DEFER, None, None, None)


def decide(end, verdict, spent, base_memory=None, base_ephemeral=None):
    """What to do about a failed attempt. `spent` is this range's cause counts.

    A cause with no budget is condemned on sight -- that is how a genuine
    catchup failure ends a run instead of burning 20 attempts proving the chain
    is broken.
    """
    cause = verdict.get('outcome')
    cap = config.ATTEMPT_BUDGETS.get(cause)
    if cap is None:
        return Decision(CONDEMN, f"{cause} is not retryable", None, None)

    # This verdict is already counted, so the Nth failure of a cause is the one
    # that exhausts a budget of N. Per cause: evictions cannot drain the budget
    # OOMs are entitled to.
    if spent.get(cause, 0) >= cap:
        return Decision(CONDEMN, f"{cause} budget of {cap} exhausted", None, None)

    memory = ephemeral = None
    if cause == 'oom':
        memory = sizing.next_memory(end, base_memory, spent.get('oom', 0))
    elif cause == 'ephemeral':
        ephemeral = sizing.next_ephemeral(base_ephemeral, spent.get('ephemeral', 0))
    return Decision(RETRY, cause, memory, ephemeral)
