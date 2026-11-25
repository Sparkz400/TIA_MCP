"""Offline checks of the independent subtree oracle in qualify_watch_lifecycle.py; nothing here starts TIA."""
import unittest

import qualify_watch_lifecycle as q

ROOT = ['group\tG', 'watch\tG/T\th1\t2', 'watch\tW\th0\t1']


class LifecycleOracleTests(unittest.TestCase):
    def test_profiles_follow_the_worker_naming(self):
        self.assertEqual('watch.table-create.v1', q.profile('createTable'))
        self.assertEqual('watch.group-rename.v1', q.profile('renameGroup'))

    def test_expect_each_operation(self):
        self.assertEqual(sorted(ROOT + ['group\tH']), q.expect(ROOT, 'createGroup', 'H', None, None))
        self.assertEqual(['group\tG2', 'watch\tG2/T\th1\t2', 'watch\tW\th0\t1'], q.expect(ROOT, 'renameGroup', 'G', 'G2', None))
        self.assertEqual(['group\tG', 'watch\tG/T\th1\t2', 'watch\tV\th9\t1'], q.expect(ROOT, 'renameTable', 'W', 'V', 'h9'))
        self.assertEqual(['group\tG', 'watch\tG/T\th1\t2'], q.expect(ROOT, 'deleteTable', 'W', None, None))
        with self.assertRaises(RuntimeError):
            q.expect(ROOT, 'deleteGroup', 'G', None, None)

    def test_created_table_hash_is_a_wildcard_only(self):
        expected = q.expect([], 'createTable', 'T', None, None)
        self.assertTrue(q.matches(expected, ['watch\tT\tabc\t0']))
        self.assertFalse(q.matches(expected, ['watch\tT\tabc\t1']))
        self.assertFalse(q.matches(expected, ['watch\tT\tabc\t0', 'group\tX']))

    def test_scoped_subtrees_compare_across_selectors(self):
        group = ['watch\tT\th1\t2']
        self.assertEqual(q.within(q.absolute(group, 'root/G'), 'root/G'), q.within(q.absolute(ROOT, 'root'), 'root/G'))


if __name__ == '__main__':
    unittest.main()
