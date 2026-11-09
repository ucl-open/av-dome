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
