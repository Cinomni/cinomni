"""What the control port is allowed to make this process write."""

import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import paths  # noqa: E402 - the path above is what makes this importable outside the image.


class ConfineTests(unittest.TestCase):
    def setUp(self):
        self.root = os.path.realpath(tempfile.mkdtemp(prefix="cinomni-confine-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def test_an_empty_candidate_is_the_root_itself(self):
        self.assertEqual(self.root, paths.confine(self.root, ""))
        self.assertEqual(self.root, paths.confine(self.root, None))

    def test_a_relative_subdirectory_resolves_under_the_root(self):
        self.assertEqual(os.path.join(self.root, "movies"), paths.confine(self.root, "movies"))

    def test_the_absolute_form_of_the_root_is_accepted(self):
        # This is the ordinary case in production: the backend sends the same absolute staging path
        # both containers see, and it has to be accepted rather than read as an escape.
        self.assertEqual(self.root, paths.confine(self.root, self.root))

    def test_traversal_out_of_the_root_is_refused(self):
        self.assertIsNone(paths.confine(self.root, "../elsewhere"))
        self.assertIsNone(paths.confine(self.root, "movies/../../elsewhere"))

    def test_an_absolute_path_outside_the_root_is_refused(self):
        self.assertIsNone(paths.confine(self.root, os.path.join(os.sep, "etc")))

    def test_a_sibling_whose_name_merely_starts_with_the_root_is_refused(self):
        # The prefix trap: "/data/downloads-elsewhere" starts with "/data/downloads" as a string and
        # is a different directory. Comparing on the separator is what catches it.
        sibling = self.root + "-elsewhere"
        os.makedirs(sibling, exist_ok=True)
        self.addCleanup(shutil.rmtree, sibling, ignore_errors=True)

        self.assertIsNone(paths.confine(self.root, sibling))

    @unittest.skipUnless(hasattr(os, "symlink"), "the platform has no symlinks")
    def test_a_symlink_pointing_out_of_the_root_is_refused(self):
        outside = os.path.realpath(tempfile.mkdtemp(prefix="cinomni-outside-"))
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        link = os.path.join(self.root, "escape")
        try:
            os.symlink(outside, link, target_is_directory=True)
        except (OSError, NotImplementedError) as error:  # Windows without developer mode
            self.skipTest(f"symlinks unavailable: {error}")

        self.assertIsNone(paths.confine(self.root, "escape"))
        self.assertIsNone(paths.confine(self.root, "escape/deeper"))

    def test_no_root_refuses_everything(self):
        self.assertIsNone(paths.confine("", "movies"))

    def test_is_inside_agrees_with_confine(self):
        self.assertTrue(paths.is_inside(self.root, "movies"))
        self.assertFalse(paths.is_inside(self.root, "../elsewhere"))

    def test_the_refusal_message_does_not_repeat_the_path(self):
        # An error that echoed the rejected path would carry somebody else's directory layout into
        # whatever an operator pastes into an issue.
        self.assertNotIn("/", paths.REJECTED.replace("sidecar's", ""))


@unittest.skipUnless(hasattr(os, "O_DIRECTORY"), "opening a directory as a descriptor is a POSIX rule")
class OpenedDirectoryTests(unittest.TestCase):
    """Confinement re-asserted on the handle, because the check and the write are not simultaneous."""

    def setUp(self):
        self.root = os.path.realpath(tempfile.mkdtemp(prefix="cinomni-open-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def test_a_real_directory_under_the_root_is_accepted(self):
        target = os.path.join(self.root, "downloads")
        os.makedirs(target, exist_ok=True)

        self.assertTrue(paths.opened_directory_is_inside(self.root, target))

    def test_the_root_itself_is_accepted(self):
        self.assertTrue(paths.opened_directory_is_inside(self.root, self.root))

    def test_a_directory_that_does_not_exist_is_refused(self):
        self.assertFalse(paths.opened_directory_is_inside(self.root, os.path.join(self.root, "absent")))

    @unittest.skipUnless(hasattr(os, "symlink"), "the platform has no symlinks")
    def test_a_directory_swapped_for_a_symlink_out_of_the_root_is_refused(self):
        # The ordering this closes: the path was confined, and only then did the last component
        # become a link to somewhere else. O_NOFOLLOW refuses the handle outright.
        outside = os.path.realpath(tempfile.mkdtemp(prefix="cinomni-outside-"))
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        swapped = os.path.join(self.root, "downloads")
        try:
            os.symlink(outside, swapped, target_is_directory=True)
        except (OSError, NotImplementedError) as error:  # Windows without developer mode
            self.skipTest(f"symlinks unavailable: {error}")

        self.assertFalse(paths.opened_directory_is_inside(self.root, swapped))


class SanitizeNameTests(unittest.TestCase):
    def test_keeps_an_ordinary_name(self):
        self.assertEqual("Some.Release.2026.mkv", paths.sanitize_name("Some.Release.2026.mkv"))

    def test_strips_directory_components(self):
        self.assertEqual("payload", paths.sanitize_name("../../etc/payload"))
        self.assertEqual("payload", paths.sanitize_name("C:\\Windows\\payload"))

    def test_refuses_to_produce_a_traversal_or_hidden_segment(self):
        self.assertEqual("download", paths.sanitize_name(".."))
        self.assertEqual("download", paths.sanitize_name("."))
        self.assertEqual("download", paths.sanitize_name(""))
        self.assertEqual("download", paths.sanitize_name(None))

    def test_drops_control_characters(self):
        self.assertEqual("name", paths.sanitize_name("na\x00m\x07e"))


if __name__ == "__main__":
    unittest.main()
