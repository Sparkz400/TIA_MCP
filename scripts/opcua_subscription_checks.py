"""Independent subscription envelope, fixture constants and service-boundary oracle."""
import copy
from opcua_runtime_checks import require, verify_values, ALLOWED

SUBSCRIPTION_SERVICES = {"CreateSubscription", "CreateMonitoredItems", "Publish", "Republish", "DeleteSubscriptions"}
FIXTURE_OBSERVATIONS = {"SubscriptionCreated", "ItemsCreated", "PublishReturned", "RepublishRequested", "SubscriptionDeleteRequested", "FixtureDroppedPublish", "PublishEntered", "PublishCompleted", "FixtureLostPublishResponse", "FixtureEventEmitted", "FixtureEventFilter"}


def verify_subscription(rows, attempt, expected_count=14):
    events = [r["event"] for r in rows]
    created = next(e["created"] for e in events if e["kind"] == "created")
    require(created["revisedPublishingInterval"] == 200 and created["revisedKeepAliveCount"] == 3 and created["revisedLifetimeCount"] >= 60, "independent revised subscription settings")
    require(len(created["items"]) == expected_count + 1 and created["items"][-1]["statusCode"] == "0x80340000" and created["items"][-1]["monitoredItemId"] == 0, "independent per-item failure")
    require(all(i["revisedSamplingInterval"] == 200 and i["revisedQueueSize"] == 4 for i in created["items"][:-1]), "independent revised item settings")
    publishes = [e for e in events if e["kind"] == "publish"]
    # Values queued after the first publishing cycle (a loaded machine) arrive after keep-alives, which carry the next sequence number, 1.
    leading = next((i for i, e in enumerate(publishes) if not e["keepAlive"]), len(publishes))
    require(all(e["keepAlive"] and e["sequenceNumber"] == 1 and not e.get("notifications") and not e["duplicate"] for e in publishes[:leading]), "keep-alives only precede the first values")
    messages = publishes[leading:]
    # A Publish message carries up to maximumNotificationsPerPublish (default 32) notifications; one row keeps one message exactly.
    require([e["sequenceNumber"] for e in messages] == list(range(1, len(messages) + 1)) and messages, "independent original sequence oracle")
    require(all(not e["duplicate"] and not e["keepAlive"] and 1 <= len(e["notifications"]) <= 32 for e in messages), "bounded original notifications per service")
    require(sum(len(e["notifications"]) for e in messages) == expected_count, "every selected signal notified once")
    require(all(e["acknowledgedSequenceNumbers"] == [index] and e["acknowledgementResults"] == ["0x00000000"] for index, e in enumerate(messages[1:], 1)), "independent previous-message acknowledgement oracle")
    values = [n["value"] for e in messages for n in e["notifications"]]
    verify_values(dict(attemptId=attempt, startedAt=rows[0]["observedUtc"], completedAt=rows[-1]["observedUtc"], items=values))
    for n in (n for e in messages for n in e["notifications"]):
        item = created["items"][n["clientHandle"] - 1]
        require(item["node"] == n["value"]["node"] and item["monitoredItemId"] == n["monitoredItemId"] and not n["overflow"], "original client/server item association")
    require(events[-1]["kind"] == "deleted" and events[-1]["statusCode"] == "0x00000000" and events[-1]["subscriptionId"] == created["subscriptionId"], "independent owned subscription deletion")


def verify_subscription_audit(audit, healthy=False):
    ops = [x["operation"] for x in audit]
    require(set(ops) <= ALLOWED | SUBSCRIPTION_SERVICES | FIXTURE_OBSERVATIONS, "subscription read-only service allowlist")
    if healthy:
        require(ops[:3] == ["GetEndpoints", "CreateSession", "ActivateSession"], "original encrypted session sequence")
        require(SUBSCRIPTION_SERVICES - {"Republish"} <= set(ops), "actual subscription services required")
        require("CloseSession" in ops, "owned CloseSession service observed")
    require(not {"TransferSubscriptions", "Write", "Call", "HistoryRead"} & set(ops), "no transfer or mutation")


def subscription_controls(rows, attempt, audit, expected_count=14):
    refused = []
    def first(r): return next(x["event"] for x in r if x["event"]["kind"] == "publish" and not x["event"]["keepAlive"])
    def value(r): return first(r)["notifications"][0]
    for name, mutate in [
        ("sequence", lambda r: first(r).update(sequenceNumber=999)),
        ("itemAssociation", lambda r: value(r).update(monitoredItemId=999999)),
        ("roundedInteger", lambda r: value(r)["value"].update(value="-9223372036854776000")),
        ("overflow", lambda r: value(r).update(overflow=True)),
        ("revisedInterval", lambda r: r[0]["event"]["created"].update(revisedPublishingInterval=123)),
        ("cleanup", lambda r: r[-1]["event"].update(statusCode="0x800a0000")),
    ]:
        altered = copy.deepcopy(rows); mutate(altered)
        try: verify_subscription(altered, attempt, expected_count)
        except (ValueError, KeyError, TypeError): refused.append(name)
        else: raise ValueError("Independent oracle accepted mutation " + name)
    for operation in ["Write", "TransferSubscriptions", "Call"]:
        try: verify_subscription_audit(audit + [dict(operation=operation)], healthy=True)
        except ValueError: refused.append(operation)
        else: raise ValueError("Independent audit accepted " + operation)
    return refused
