"""Check that relative links and #anchors in the tracked Markdown files resolve, and that docs/wiki keeps its link rules."""
import html
import pathlib
import posixpath
import re
import subprocess
import unittest
import urllib.parse


ROOT = pathlib.Path(__file__).resolve().parents[2]
REPOSITORY = "Sparkz400/TIA_MCP"

# Not checked: these trees are kept byte for byte (.gitattributes: -text), so a link in them could not be repaired.
SKIPPED = (
    "fixtures/",       # retained fixtures
    "third-party/",    # vendored texts, as supplied
)

FENCE = re.compile(r"^[ \t>]*(`{3,}|~{3,})")
CODE_SPAN = re.compile(r"(?<!`)(`+)(?!`)((?:(?!\n[ \t]*\n).)+?)(?<!`)\1(?!`)", re.S)
COMMENT = re.compile(r"<!--.*?-->", re.S)
# [text](target "title") and ![alt](target); the target may be <bracketed> or hold one level of parentheses.
INLINE_LINK = re.compile(r"\]\(\s*(<[^<>\n]*>|[^\s()<>]*(?:\([^\s()]*\)[^\s()<>]*)*)(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)")
REFERENCE_DEFINITION = re.compile(r"^ {0,3}\[[^\]\n]+\]:[ \t]*(<[^<>\n]*>|\S+)", re.M)
HTML_LINK = re.compile(r"<(?:a|img)\b[^>]*?\b(?:href|src)\s*=\s*(?:\"([^\"]*)\"|'([^']*)')", re.I)
HTML_ANCHOR = re.compile(r"<[a-z][^>]*?\b(?:id|name)\s*=\s*(?:\"([^\"]*)\"|'([^']*)')", re.I)
WIKI_LINK = re.compile(r"\[\[([^\[\]\n]+)\]\]")
ATX_HEADING = re.compile(r"^ {0,3}#{1,6}[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")
SETEXT_UNDERLINE = re.compile(r"^ {0,3}(=+|-+)[ \t]*$")
SCHEME = re.compile(r"^[a-z][a-z0-9+.-]*:", re.I)
OWN_URL = re.compile(r"^https://github\.com/" + re.escape(REPOSITORY) + r"/(blob|tree)/([^/#?]+)/([^#?]*)(?:\?[^#]*)?(?:#(.*))?$", re.I)
OWN_RAW_URL = re.compile(r"^https://raw\.githubusercontent\.com/" + re.escape(REPOSITORY) + r"/([^/]+)/([^#?]+)", re.I)


def blank(match):
    """Replace a match by spaces, keeping line breaks so that offsets still give line numbers."""
    return re.sub(r"[^\n]", " ", match.group(0))


def without_fences(text):
    """The text with fenced code blocks blanked; line count and offsets are unchanged."""
    lines, fence = [], None
    for line in text.split("\n"):
        opened = FENCE.match(line)
        if fence is None:
            if opened:
                fence = opened.group(1)
                line = ""
        else:
            if opened and opened.group(1)[0] == fence[0] and len(opened.group(1)) >= len(fence) and not line[opened.end():].strip():
                fence = None
            line = ""
        lines.append(line)
    return "\n".join(lines)


def heading_text(source):
    """The rendered text of a heading's Markdown source, as far as its anchor depends on it."""
    text = re.sub(r"!?\[((?:[^\[\]]|\[[^\[\]]*\])*)\]\([^()]*(?:\([^()]*\)[^()]*)*\)", r"\1", source)  # links and images
    text = re.sub(r"!?\[([^\[\]]*)\]\[[^\[\]]*\]", r"\1", text)  # reference links
    text = re.sub(r"</?[a-zA-Z][^<>]*>", "", text)  # inline HTML
    text = text.replace("`", "").replace("*", "")
    # Underscores are emphasis only at a word edge; inside snake_case they are text.
    text = re.sub(r"(?<![^\W_])_+(?=\S)|(?<=\S)_+(?![^\W_])", "", text)
    text = re.sub(r"\\([!-/:-@\[-`{-~])", r"\1", text)  # backslash escapes
    return html.unescape(text)


def slug(text):
    """GitHub's heading anchor: lower case, drop everything but word characters, hyphens and spaces, spaces to hyphens."""
    return re.sub(r"[^\w\- ]", "", text.strip().lower()).replace(" ", "-")


def anchors(text):
    """Every anchor GitHub renders for a Markdown text: heading slugs (repeats get -1, -2, ...) and explicit HTML ids."""
    found, seen, previous = set(), {}, ""
    visible = COMMENT.sub(blank, without_fences(text))
    for line in visible.split("\n"):
        heading = ATX_HEADING.match(line)
        title = heading.group(1) if heading else None
        # A setext underline makes the paragraph line above it a heading; table rows and list items are not paragraphs.
        if title is None and SETEXT_UNDERLINE.match(line) and previous.strip() and not re.match(r"^\s*([|>#]|[-*+] |\d+[.)] )", previous):
            title = previous.strip()
        if title is not None:
            base = slug(heading_text(title))
            count = seen.get(base, 0)
            seen[base] = count + 1
            found.add(base if count == 0 else "%s-%d" % (base, count))
        previous = line
    for match in HTML_ANCHOR.finditer(visible):
        found.add(match.group(1) or match.group(2) or "")
    return found


def links(text):
    """(line, target) of every inline link, image, reference definition and HTML href/src outside code."""
    visible = CODE_SPAN.sub(blank, COMMENT.sub(blank, without_fences(text)))
    result = []
    for pattern in (INLINE_LINK, REFERENCE_DEFINITION, HTML_LINK):
        for match in pattern.finditer(visible):
            target = next(group for group in match.groups() if group is not None)
            if target.startswith("<") and target.endswith(">"):
                target = target[1:-1]
            result.append((visible.count("\n", 0, match.start()) + 1, html.unescape(target)))
    return sorted(result)


def wiki_links(text):
    """(line, page) of every [[Page]] or [[text|Page]] link outside code."""
    visible = CODE_SPAN.sub(blank, COMMENT.sub(blank, without_fences(text)))
    return [(visible.count("\n", 0, match.start()) + 1, match.group(1).split("|")[-1].strip()) for match in WIKI_LINK.finditer(visible)]


class Repository:
    """The tracked files; existence is judged against Git's list, which is case-sensitive like GitHub."""

    def __init__(self, root=ROOT):
        listed = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, check=True).stdout
        self.root = root
        self.files = {name for name in listed.decode("utf-8").split("\0") if name}
        self.folders = {""}
        for name in self.files:
            while "/" in name:
                name = name.rsplit("/", 1)[0]
                self.folders.add(name)
        self.cache = {}

    def markdown(self):
        return sorted(name for name in self.files if name.lower().endswith(".md") and not name.startswith(SKIPPED))

    def text(self, name):
        return (self.root / name).read_text(encoding="utf-8")

    def anchors(self, name):
        if name not in self.cache:
            self.cache[name] = anchors(self.text(name))
        return self.cache[name]

    def defect(self, path, fragment):
        """Why a repository path with an optional #fragment does not resolve, or None."""
        path = path.rstrip("/")
        if path not in self.files and path not in self.folders:
            return "no tracked file or folder '%s'" % path
        # Only Markdown anchors are known here; '#L10' on a source file is a line number.
        if fragment and path.lower().endswith(".md") and urllib.parse.unquote(fragment) not in self.anchors(path):
            return "no anchor '#%s' in %s" % (fragment, path)
        return None

    def relative_defect(self, source, target):
        """Why a relative link in the tracked file `source` does not resolve, or None."""
        path, _, fragment = target.partition("#")
        path = urllib.parse.unquote(path.split("?", 1)[0])
        if not path:
            resolved = source
        elif path.startswith("/"):
            resolved = posixpath.normpath(path.lstrip("/"))  # GitHub resolves a leading slash from the repository root
        else:
            resolved = posixpath.normpath(posixpath.join(posixpath.dirname(source), path))
        if resolved == ".":
            resolved = ""
        if resolved.startswith(".."):
            return "leaves the repository"
        return self.defect(resolved, fragment)


class SlugTests(unittest.TestCase):
    def test_heading_anchors_follow_github(self):
        text = "\n".join([
            "# Getting Started",
            "## What's new in v2.0?",
            "## `project_open` and the UMAC route",
            "## Step 1: build & run",          # '&' goes, both spaces stay: two hyphens
            "## A [linked](other.md#x) *title* with _emphasis_",
            "## snake_case_name stays",
            "## Größe – Übersicht",             # letters of any script stay, the dash goes
            "## Trailing hashes ##",
            "## Repeat",
            "## Repeat",
            "### Repeat",
            "Setext title",
            "------------",
            "| table | row |",
            "|-------|-----|",
            "```",
            "## Not a heading",
            "```",
            "<!-- ## Not a heading either -->",
            '<a id="explicit-id"></a>',
        ])
        self.assertEqual({
            "getting-started", "whats-new-in-v20", "project_open-and-the-umac-route", "step-1-build--run",
            "a-linked-title-with-emphasis", "snake_case_name-stays", "größe--übersicht", "trailing-hashes",
            "repeat", "repeat-1", "repeat-2", "setext-title", "explicit-id"}, anchors(text))

    def test_links_outside_code_are_found(self):
        text = "\n".join([
            "See [one](a.md) and [two\nlines](b.md#x \"title\") and ![image](c%20d.png).",
            "`[code](ignored.md)` and <a href=\"e.md\">html</a> and [paren](f_(g).md).",
            "```",
            "[fenced](ignored.md)",
            "```",
            "[reference]: <h i.md>",
            "[[Getting Started]] and [[the FAQ|FAQ]]",
        ])
        self.assertEqual([(1, "a.md"), (2, "b.md#x"), (2, "c%20d.png"), (3, "e.md"), (3, "f_(g).md"), (7, "h i.md")], links(text))
        self.assertEqual([(8, "Getting Started"), (8, "FAQ")], wiki_links(text))


@unittest.skipUnless((ROOT / ".git").exists(), "Needs the Git checkout: link targets are checked against the tracked files.")
class MarkdownLinkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.repository = Repository()

    def test_relative_links_and_anchors_resolve(self):
        repository, broken = self.repository, []
        for name in repository.markdown():
            for line, target in links(repository.text(name)):
                own = OWN_URL.match(target)
                if own and own.group(2) == "main":
                    # An absolute link to this repository's main branch must name a file of this tree.
                    defect = repository.defect(urllib.parse.unquote(own.group(3)), own.group(4))
                elif SCHEME.match(target) or target.startswith("//"):
                    continue  # external; never fetched here
                else:
                    defect = repository.relative_defect(name, target)
                if defect:
                    broken.append("%s:%d: (%s) %s" % (name, line, target, defect))
        self.assertFalse(broken, "Broken Markdown links:\n" + "\n".join(broken))

    def test_wiki_pages_link_absolutely_and_by_page_name(self):
        # AGENTS.md: relative links do not resolve on the wiki, so repository files are linked as
        # https://github.com/<repository>/blob/main/... and wiki pages as [[Page Name]].
        repository, broken = self.repository, []
        pages = [name for name in repository.markdown() if name.startswith("docs/wiki/")]
        self.assertTrue(pages)
        for name in pages:
            text = repository.text(name)
            for line, target in links(text):
                own, raw = OWN_URL.match(target), OWN_RAW_URL.match(target)
                if target.startswith("#"):
                    continue  # an anchor on the same page; checked by the test above
                if not SCHEME.match(target):
                    broken.append("%s:%d: (%s) relative link; use the absolute blob/main URL or [[Page Name]]" % (name, line, target))
                elif own and (own.group(1).lower(), own.group(2)) not in (("blob", "main"), ("tree", "main")):
                    broken.append("%s:%d: (%s) repository link is not on main" % (name, line, target))
                elif own and own.group(3).startswith("docs/wiki/"):
                    broken.append("%s:%d: (%s) links the wiki source; use [[Page Name]]" % (name, line, target))
                elif raw and (raw.group(1) != "main" or urllib.parse.unquote(raw.group(2)) not in repository.files):
                    broken.append("%s:%d: (%s) raw link does not name a tracked file on main" % (name, line, target))
            for line, page in wiki_links(text):
                if "docs/wiki/%s.md" % page.replace(" ", "-") not in repository.files:
                    broken.append("%s:%d: [[%s]] names no page in docs/wiki" % (name, line, page))
        self.assertFalse(broken, "Wiki link rules broken:\n" + "\n".join(broken))

    def test_wiki_pages_are_listed(self):
        # AGENTS.md: new pages are listed in _Sidebar.md and Home.md.
        repository = self.repository
        pages = {pathlib.PurePosixPath(name).stem.replace("-", " ") for name in repository.files
                 if name.startswith("docs/wiki/") and name.endswith(".md") and not posixpath.basename(name).startswith("_")}
        for index in ("docs/wiki/_Sidebar.md", "docs/wiki/Home.md"):
            listed = {page for _, page in wiki_links(repository.text(index))}
            self.assertFalse(pages - listed - {pathlib.PurePosixPath(index).stem}, index + " does not list every wiki page.")


if __name__ == "__main__":
    unittest.main()
