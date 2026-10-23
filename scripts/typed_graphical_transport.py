"""Use the deployed MCP transport and hash-bound remote artifact reads unchanged."""
import base64
import hashlib


def artifact_bytes(call, artifact):
    """Read only through MCP; engineering-host paths are never client paths."""
    if not isinstance(artifact, dict) or not isinstance(artifact.get("path"), str):
        raise ValueError("Prepared artifact must include its engineering-host path.")
    expected_hash, expected_size = artifact.get("sha256"), artifact.get("bytes")
    if (not isinstance(expected_hash, str) or len(expected_hash) != 64
            or not isinstance(expected_size, int) or isinstance(expected_size, bool)
            or not 0 <= expected_size <= 16 * 1024 * 1024):
        raise ValueError("Prepared artifact must include a bounded immutable hash/length manifest.")
    contents = bytearray()
    for _ in range(257):
        offset = len(contents)
        piece = call("artifact_read", {"path": artifact["path"], "offset": offset, "count": 65536})
        if (piece.get("offset") != offset or piece.get("encoding") != "base64"
                or piece.get("sha256") != expected_hash or piece.get("bytes") != expected_size):
            raise ValueError("Prepared artifact changed or the transfer identity/offset is invalid.")
        data = base64.b64decode(piece.get("data", ""), validate=True)
        if (len(data) > 65536 or piece.get("count") != len(data)
                or piece.get("nextOffset") != offset + len(data)
                or offset + len(data) > expected_size):
            raise ValueError("Prepared artifact transfer violated byte boundaries.")
        contents.extend(data)
        if piece.get("complete") is True:
            if len(contents) != expected_size or hashlib.sha256(contents).hexdigest() != expected_hash:
                raise ValueError("Prepared artifact complete hash/length does not match its manifest.")
            return bytes(contents)
        if not data or len(contents) >= expected_size:
            raise ValueError("Prepared artifact transfer stalled or omitted completion.")
    raise ValueError("Prepared artifact transfer exceeded its bound.")
