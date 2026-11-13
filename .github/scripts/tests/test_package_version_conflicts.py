import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from resolve_package_version_conflicts import resolve_file, resolve_text


def package(name, version):
    return f'    <PackageReference Include="{name}" Version="{version}" />\n'


def conflict(ours, theirs):
    return '<<<<<<< HEAD\n' + ours + '=======\n' + theirs + '>>>>>>> upstream/master\n'


class PackageVersionConflictTests(unittest.TestCase):
    def test_anglesharp_upgrade_preserves_vendored_solver(self):
        reference = '    <ProjectReference Include="..\\FlareSolverrSharp\\FlareSolverrSharp.csproj" />\n'
        text = '<Project>\n' + conflict(package('AngleSharp', '1.8.3'), package('AngleSharp', '1.8.4')) + reference + '</Project>\n'
        resolved, count = resolve_text(text)
        self.assertEqual(count, 1)
        self.assertEqual(resolved, '<Project>\n' + package('AngleSharp', '1.8.4') + reference + '</Project>\n')

    def test_dotnet_upgrade_retains_newer_fork_test_tools(self):
        exclusion = '    <Compile Remove="Common\\Utils\\FlareSolverrIntegrationTests.cs" />\n'
        ours = package('NUnit.ConsoleRunner', '3.22.0') + package('NUnit3TestAdapter', '4.6.0') + package('System.Text.Encoding.CodePages', '9.0.20')
        theirs = package('NUnit.ConsoleRunner', '3.17.0') + package('NUnit3TestAdapter', '4.5.0') + package('System.Text.Encoding.CodePages', '9.0.21')
        resolved, _ = resolve_text(exclusion + conflict(ours, theirs))
        expected = exclusion + package('NUnit.ConsoleRunner', '3.22.0') + package('NUnit3TestAdapter', '4.6.0') + package('System.Text.Encoding.CodePages', '9.0.21')
        self.assertEqual(resolved, expected)

    def test_multiple_conflicts_and_numeric_ordering(self):
        text = conflict(package('A', '1.9.0'), package('A', '1.10.0')) + conflict(package('B', '2.0'), package('B', '2.0.0'))
        resolved, count = resolve_text(text)
        self.assertEqual(count, 2)
        self.assertEqual(resolved, package('A', '1.10.0') + package('B', '2.0'))

    def test_retains_original_crlf_outside_and_inside_conflicts(self):
        text = ('<Project>\n' + conflict(package('A', '1.0'), package('A', '2.0')) + '</Project>\n').replace('\n', '\r\n')
        resolved, _ = resolve_text(text)
        self.assertEqual(resolved, ('<Project>\n' + package('A', '2.0') + '</Project>\n').replace('\n', '\r\n'))

    def test_real_project_changes_remain_manual(self):
        for ours, theirs in (
            (package('A', '1.0'), package('B', '2.0')),
            (package('A', '1.0'), package('A', '2.0') + package('B', '1.0')),
            ('<TargetFramework>net9.0</TargetFramework>\n', '<TargetFramework>net10.0</TargetFramework>\n'),
            (package('A', '1.0'), package('A', '2.0').replace('/>', 'PrivateAssets="all" />')),
        ):
            with self.subTest(ours=ours, theirs=theirs), self.assertRaises(ValueError):
                resolve_text(conflict(ours, theirs))

    def test_non_numeric_version_changes_remain_manual(self):
        for version in ('2.0-preview.1', '2.0+metadata', '[2.0,3.0)', '$(PackageVersion)', '*'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                resolve_text(conflict(package('A', '1.0'), package('A', version)))

    def test_incomplete_and_diff3_conflicts_remain_manual(self):
        for text in (
            conflict(package('A', '1.0'), package('A', '2.0')).replace('>>>>>>> upstream/master\n', ''),
            conflict(package('A', '1.0') + '||||||| base\n' + package('A', '0.9'), package('A', '2.0')),
            package('A', '1.0'),
        ):
            with self.subTest(text=text), self.assertRaises(ValueError):
                resolve_text(text)

    def test_file_is_untouched_when_a_later_conflict_is_not_safe(self):
        text = conflict(package('A', '1.0'), package('A', '2.0')) + conflict('<Property>ours</Property>\n', '<Property>theirs</Property>\n')
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory) / 'Test.csproj'
            project.write_bytes(text.encode('utf-8'))
            with self.assertRaises(ValueError):
                resolve_file(project)
            self.assertEqual(project.read_bytes(), text.encode('utf-8'))


if __name__ == '__main__':
    unittest.main()
