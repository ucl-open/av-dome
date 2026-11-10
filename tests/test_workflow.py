"""Bugs found in src\\main.bonsai stay fixed."""

import xml.etree.ElementTree as ET
from pathlib import Path

W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
MAIN = ET.parse(Path(__file__).parents[1] / "src" / "main.bonsai").getroot()
FINITE = ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")


def test_no_async_event_sender_completes_async_events():
    # A MulticastSubject passes on its source's completion. RngSeed's Take(1) sat ahead of its
    # multicast and completed the shared AsyncEvents subject at trial 1: nothing later (rewards,
    # Stop) reached BonsaiData_Async (found by Tom Childs on coen-dome-navigation, 9 Nov 2026).
    # A sender that logs once takes its one value after the multicast.
    senders = 0
    for workflow in MAIN.iter(W + "Workflow"):
        nodes = list(workflow.find(W + "Nodes"))
        edges = [(int(e.get("From")), int(e.get("To"))) for e in workflow.find(W + "Edges")]
        for i, node in enumerate(nodes):
            if node.get(XSI) != "MulticastSubject" or node.findtext(W + "Name") != "AsyncEvents":
                continue
            senders += 1
            upstream, frontier = set(), [i]
            while frontier:
                to = frontier.pop()
                for a, b in edges:
                    if b == to and a not in upstream:
                        upstream.add(a)
                        frontier.append(a)
            finite = [c.get(XSI) for a in upstream
                      if (c := nodes[a].find(W + "Combinator")) is not None and c.get(XSI) in FINITE]
            assert not finite, f"an AsyncEvents sender completes after {finite}"
    assert senders == 3  # RngSeed, RewardDelivered, Control


DELAYS = ("rx:Delay", "rx:DelaySubscription", "gl:Timer", "rx:Timer")


def test_the_stimulus_is_commanded_as_execute_trial_starts():
    # Audit, 10 Nov 2026, round 4: laserToStimulusSeconds said the stimulus comes 0.1-0.25 s after the wait (the
    # quiescent draw, which nothing reads). StimuliPresentation stamps stimulusOnsetTime with no delay in between.
    scope = next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == "StimuliPresentation")
    workflow = scope.find(W + "Workflow")
    nodes = list(workflow.find(W + "Nodes"))
    edges = [(int(e.get("From")), int(e.get("To"))) for e in workflow.find(W + "Edges")]
    onset = next(i for i, n in enumerate(nodes)
                 if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "stimulusOnsetTime")
    upstream, frontier = set(), [onset]
    while frontier:
        to = frontier.pop()
        sources = [a for a, b in edges if b == to]
        if nodes[to].get(XSI) == "SubscribeSubject":  # through the scope's own subject (settings) to what feeds it
            name = nodes[to].findtext(W + "Name")
            sources += [i for i, n in enumerate(nodes) if n.get(XSI, "").startswith("rx:") and n.findtext(W + "Name") == name]
        for a in sources:
            if a not in upstream:
                upstream.add(a)
                frontier.append(a)
    assert any(nodes[i].get(XSI) == "WorkflowInput" for i in upstream)
    for i in upstream:
        node = nodes[i]
        assert node.get("Path") != r"Extensions\HarpDelay.bonsai"
        assert not any(c.get(XSI) in DELAYS for c in node.iter() if c.get(XSI))
    assert "Quiescent" not in ET.tostring(MAIN, encoding="unicode")


def test_never_read_fields_say_so():
    from ucl_open_av_dome.task import RewardSettings, TrialStructure
    for model, field in ((TrialStructure, "quiescent_min_seconds"), (TrialStructure, "quiescent_max_seconds"),
                         (RewardSettings, "reward_probability")):
        assert "Not used" in model.model_fields[field].description
