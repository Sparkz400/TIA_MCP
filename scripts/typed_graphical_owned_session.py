"""Tracked local typed qualification lifetimes; no attach, cancellation or replay."""
import pathlib

from qualification_owned_scratch import OwnedScratch, require
from run_block_tests import binary_manifest
from smoke_mcp import Client


class TypedOwnedSession(OwnedScratch):
    def __init__(self, host, worker, artifacts, report, persist, *, timeout=300,
                 sim_worker=None, extra_tools=(), **kwargs):
        selected_sim = pathlib.Path(sim_worker).resolve() if sim_worker else None
        def client_factory(executable, bound, arguments):
            if selected_sim:
                arguments = [*arguments, "--sim-worker", str(selected_sim)]
            return Client(executable, bound, arguments)
        kwargs.setdefault("client_factory", client_factory)
        kwargs.setdefault("manifest_factory", lambda h, w: binary_manifest(h, w, selected_sim))
        super().__init__(host, worker, artifacts, report, persist, timeout=timeout,
            extra_tools=set(extra_tools) | {"project_retrieve"}, **kwargs)

    def invoke(self, name, arguments=None):
        arguments = arguments or {}
        if name == "tia_start":
            require(not arguments, "Owned start accepts no alternate process target.")
            return self.start()
        if name == "tia_detach":
            require(not arguments, "Owned detach accepts no alternate target.")
            self.close()
            return self.session["cleanup"]["detach"]
        if name == "project_retrieve":
            require(self.current_project is None, "Archive retrieval requires an empty owned session.")
            result = super().call(name, arguments)
            require(result.get("retrieved") is True, "Archive retrieval did not complete.")
            self.remember(result["project"]["path"])
            self.guard()
            return result
        return super().call(name, arguments)
