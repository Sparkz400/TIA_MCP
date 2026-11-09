"""Shared SHA-256 helpers for evidence scripts.

Canonical forms (how a record is serialized before hashing) are per evidence
format and intentionally not shared: each frozen digest must keep the exact
bytes its own script defined when the evidence was written.
"""
import hashlib


def sha256(data: bytes) -> str:
    """Lowercase hex SHA-256 of bytes."""
    return hashlib.sha256(data).hexdigest()


def sha256_file(path) -> str:
    """Lowercase hex SHA-256 of a file's bytes, read in chunks."""
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()
