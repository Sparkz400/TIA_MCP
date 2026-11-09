"""Short output root for native qualification runs.

TIA Portal scratch projects nest deep folders, so native qualification runs
write under a short root to stay inside Windows path limits. The root is
`D:/TIAW` on the original qualification workstation; set TIA_MCP_SHORT_ROOT
to use another short folder. Run folders cited by verification records are
retained in `fixtures/evidence/tiaw/`.
"""

import os
from pathlib import Path

ENVIRONMENT = "TIA_MCP_SHORT_ROOT"
DEFAULT = "D:/TIAW"


def short_root():
    """The configured short root, resolved."""
    return Path(os.environ.get(ENVIRONMENT) or DEFAULT).resolve()
