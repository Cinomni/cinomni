"""Confining what the control port is allowed to make this process write.

The gRPC control surface takes a save path and, on a resume, a `.fastresume` blob that carries its
own save path and tracker list inside it. Both arrive over a socket. Once the sidecar lives in a
shared network namespace, "over a socket" stops meaning "from the application" and starts meaning
"from whatever else can reach that address" — so the parameters that decide where this process
writes have to be treated as hostile, and confined here rather than trusted upstream.

The rule is one line: everything this process writes lands under the data root it was given, and a
path that resolves outside it is refused. Resolution is what makes that true — `..` is the obvious
escape and a symlink is the quiet one, and only asking the filesystem where a path really goes
catches both.
"""

import os

# The refusal never echoes the rejected path. A caller that guessed a path learns whether it exists
# from a message that repeats it back, and the log line would then carry somebody else's directory
# layout into a file an operator pastes into an issue.
REJECTED = "save path is outside the sidecar's data root"


def confine(root, candidate):
    """The real path of `candidate` inside `root`, or None when it escapes.

    An empty candidate means "the root itself", which is what an add with no explicit save path
    asks for. The root is resolved too: a data root that is itself reached through a symlink must
    not make every path under it look like an escape.
    """
    if not root:
        return None

    resolved_root = os.path.realpath(root)
    target = resolved_root if not candidate else os.path.realpath(os.path.join(resolved_root, candidate))

    if target == resolved_root:
        return target

    return target if target.startswith(resolved_root + os.sep) else None


def is_inside(root, candidate):
    """Whether `candidate` resolves inside `root`. The predicate behind `confine`, for assertions."""
    return confine(root, candidate) is not None


def opened_directory_is_inside(root, directory):
    """Re-assert confinement on the directory as it is now open, not as it resolved a moment ago.

    `confine` answers about a path; this answers about a handle. Between the two the last component
    can be replaced with a symlink pointing outside the root, and every check made before that swap
    describes a directory this process is no longer about to write into. Opening with `O_NOFOLLOW`
    refuses the swapped symlink outright, and reading the handle back through `/proc/self/fd` asks
    the kernel where the descriptor actually landed.

    This is a Linux rule, as the whole process is. Where a directory cannot be opened as a
    descriptor at all the answer is a refusal, which is the direction that fails closed; where there
    is no `/proc` to read the descriptor back through, the path is resolved again rather than
    refused, because that is a hardened kernel rather than an attack.
    """
    flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        handle = os.open(directory, flags)
    except OSError:
        return False

    try:
        link = f"/proc/self/fd/{handle}"
        opened = os.path.realpath(link) if os.path.exists(link) else os.path.realpath(directory)
        return is_inside(root, opened)
    finally:
        os.close(handle)


def sanitize_name(name, fallback="download"):
    """A single path segment safe to join onto a root: no separators, no traversal, no hidden names.

    Used for the names the engine reports back rather than for the paths it is given — a torrent
    names its own content, and that name reaches the filesystem.
    """
    cleaned = (name or "").strip().replace("\\", "/")
    segment = cleaned.rsplit("/", 1)[-1]
    segment = segment.strip().strip(".")
    segment = "".join(character for character in segment if character.isprintable() and character != os.sep)
    return segment or fallback
