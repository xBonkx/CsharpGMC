import subprocess
import tempfile
import unittest
from pathlib import Path

from source_size import (band, physical_lines, partial_keys, inventory, growth,
                         project_scope, partial_growth, renamed_paths)


class SourceSizeTests(unittest.TestCase):
    def test_thresholds(self):
        for n, label in [(0, '<=500'), (500, '<=500'), (501, '501-1000'),
                         (1000, '501-1000'), (1001, '1001-1500'), (1500, '1001-1500'),
                         (1501, '1501-2000'), (2000, '1501-2000'), (2001, '2001-4000'),
                         (4000, '2001-4000'), (4001, '>4000')]:
            self.assertEqual(label, band(n))

    def test_physical_lines(self):
        self.assertEqual(0, physical_lines(''))
        self.assertEqual(3, physical_lines('a\r\n\r\n// comment'))
        self.assertEqual(1, physical_lines('a\n'))

    def test_partial_namespaces(self):
        self.assertEqual(['A.C'], partial_keys('namespace A;\npublic partial class C {}'))
        self.assertEqual(['B.C'], partial_keys('namespace B {\ninternal sealed partial class C {}\n}'))
        self.assertEqual([], partial_keys('namespace A;\nclass C {}'))
        self.assertEqual([], partial_keys('namespace A {}\nnamespace B {}\npartial class C {}'))

    def test_growth_not_existing_debt(self):
        before = {p: {'lines': n} for p, n in [('old', 4500), ('less', 4500), ('cross', 1000)]}
        after = {p: {'lines': n} for p, n in [('old', 4500), ('less', 4400), ('cross', 1001), ('new', 4001), ('small', 999)]}
        self.assertEqual(['new', 'cross'], [r['path'] for r in growth(before, after)])

    def test_rename_does_not_count_existing_debt_as_new(self):
        before = {'old.cs': {'lines': 4200}}
        renamed = {'new.cs': 'old.cs'}
        self.assertEqual([], growth(before, {'new.cs': {'lines': 4200}}, renamed))
        warning = growth(before, {'new.cs': {'lines': 4201}}, renamed)[0]
        self.assertEqual((4200, 1, 'old.cs'),
                         (warning['before'], warning['delta'], warning['previous_path']))

    def test_nearest_project_and_ambiguous_directory(self):
        dirs = {'tools': ['tools/Outer.csproj'],
                'tools/nested': ['tools/nested/Inner.csproj'],
                'src/mixed': ['src/mixed/A.csproj', 'src/mixed/B.csproj']}
        self.assertEqual('tools/nested/Inner.csproj', project_scope('tools/nested/Views/A.cs', dirs))
        self.assertEqual('tools/Outer.csproj', project_scope('tools/A.cs', dirs))
        self.assertEqual('src/mixed/<multiple-projects>', project_scope('src/mixed/A.cs', dirs))
        self.assertEqual('src/App', project_scope('src/App/A.cs', dirs))

    def test_partial_growth_detects_split_without_double_counting_baseline(self):
        def row(n, project='App.csproj'):
            return {'lines': n, 'project': project, 'partial': ['N.C']}
        before = {'A.cs': row(1200), 'Other.cs': row(900, 'Other.csproj')}
        unchanged = {'A.cs': row(600), 'B.cs': row(600),
                     'Other.cs': row(900, 'Other.csproj')}
        self.assertEqual([], partial_growth(before, unchanged))
        unchanged['B.cs'] = row(650)
        self.assertEqual([{'type': 'App.csproj:N.C', 'before': 1200,
                           'after': 1250, 'delta': 50}], partial_growth(before, unchanged))

    def test_real_git_rename(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            def run(*args):
                subprocess.run(['git', '-C', folder, *args], check=True,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            run('init')
            run('config', 'user.name', 'test')
            run('config', 'user.email', 'test@example.invalid')
            old, new = 'old name.cs', 'новое имя.cs'
            (root / old).write_text(''.join(f'// unique line {i}\n' for i in range(1200)), encoding='utf-8')
            run('add', '.')
            run('commit', '-m', 'baseline')
            run('mv', old, new)
            run('commit', '-m', 'rename')
            before, after = inventory(root, 'HEAD~1'), inventory(root, 'HEAD')
            mapping = renamed_paths(root, before['revision'], after['revision'])
            self.assertEqual({new: old}, mapping)
            self.assertEqual([], growth(before['files'], after['files'], mapping))

    def test_real_git_snapshot_and_exclusions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            def run(*args):
                subprocess.run(['git', '-C', folder, *args], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            run('init')
            run('config', 'user.name', 'test')
            run('config', 'user.email', 'test@example.invalid')
            def write(path, text):
                file = root / path
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_bytes(text.encode('utf-8'))
            write('src/App/A.cs', 'namespace N;\npartial class C {}\n')
            write('src/App/Б.cs', 'namespace N;\npartial class C {}\n')
            write('tests/App/A.cs', 'namespace N;\npartial class C {}\n')
            write('src/App/View.xaml', '<Grid/>')
            write('src/App/obj/Build.cs', 'generated')
            write('src/App/Gen.g.cs', 'generated')
            write('src/App/Header.cs', '// <auto-generated/>\n')
            run('add', '.')
            run('commit', '-m', 'baseline')
            write('src/App/A.cs', '\n' * 1001)
            run('add', '.')
            run('commit', '-m', 'growth')
            old = inventory(root, 'HEAD~1')
            new = inventory(root, 'HEAD')
            self.assertEqual(4, len(old['files']))
            self.assertEqual(3, len(old['excluded']))
            self.assertEqual(4, old['partials'][0]['lines'])
            self.assertEqual(2, len(old['partials'][0]['files']))
            self.assertEqual('src/App/A.cs', growth(old['files'], new['files'])[0]['path'])
            write('src/App/A.cs', 'uncommitted')
            self.assertEqual(1001, inventory(root, 'HEAD')['files']['src/App/A.cs']['lines'])


if __name__ == '__main__':
    unittest.main()
