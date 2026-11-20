using System;
using System.Linq;
using System.Text;
using Reports.Manager;
using Xunit;

namespace Reports.Tests
{
    public class ManagerTests
    {
        private static byte[] Repx(string script, string band = "Detail") => new UTF8Encoding(true).GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<XtraReportsLayoutSerializer SerializerVersion=\"20.1.3.0\" ControlType=\"DevExpress.XtraReports.UI.XtraReport\" " +
            $"ScriptsSource=\"{script.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;").Replace("\r\n", "&#xD;&#xA;")}\">\r\n" +
            $"  <Bands><Item1 ControlType=\"{band}Band\" Name=\"{band}\" /></Bands>\r\n" +
            "</XtraReportsLayoutSerializer>");

        [Fact]
        public void DiffFindsChangedLine()
        {
            var diff = LineDiff.Compute("a\nb\nc\nd", "a\nB\nc\nd\ne");
            Assert.Equal(2, diff.Added);
            Assert.Equal(1, diff.Removed);
            var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();
            Assert.Contains(lines, l => l.Kind == DiffKind.Removed && l.Text == "b" && l.OldNo == 2);
            Assert.Contains(lines, l => l.Kind == DiffKind.Added && l.Text == "e" && l.NewNo == 5);
        }

        [Fact]
        public void DiffOfEqualTextIsIdentical()
        {
            Assert.True(LineDiff.Compute("x\r\ny", "x\ny\n").Identical);
            Assert.True(LineDiff.Compute(null, "").Identical);
        }

        [Fact]
        public void RepxScriptIsSeparatedFromMarkup()
        {
            var text = RepxText.Parse(Repx("private void A()\r\n{\r\n    if (x < 1) return;\r\n}"));
            Assert.True(text.IsXml);
            Assert.Contains("if (x < 1) return;", text.Script);
            Assert.Equal(4, text.Script.Replace("\r\n", "\n").Split('\n').Length);
            Assert.DoesNotContain("private void A", text.Markup);
            Assert.Contains("DetailBand", text.Markup);
        }

        [Fact]
        public void ScriptChangeDoesNotTouchMarkupDiff()
        {
            var a = RepxText.Parse(Repx("int x = 1;\r\nint y = 2;"));
            var b = RepxText.Parse(Repx("int x = 1;\r\nint y = 3;"));
            Assert.True(LineDiff.Compute(a.Markup, b.Markup).Identical);
            var script = LineDiff.Compute(a.Script, b.Script);
            Assert.Equal(1, script.Added);
            Assert.Equal(1, script.Removed);
        }

        [Fact]
        public void NotXmlFallsBackToWholeText()
        {
            var text = RepxText.Parse(Encoding.UTF8.GetBytes("не xml"));
            Assert.False(text.IsXml);
            Assert.Equal("не xml", text.Markup);
        }

        [Fact]
        public void ValidateAcceptsReportAndRejectsOtherFiles()
        {
            Assert.Null(RepxText.Validate(Repx("")));
            Assert.NotNull(RepxText.Validate(Array.Empty<byte>()));
            Assert.NotNull(RepxText.Validate(Encoding.UTF8.GetBytes("<root><a/></root>")));
            Assert.NotNull(RepxText.Validate(Encoding.UTF8.GetBytes("просто текст")));
        }

        [Fact]
        public void PendingSaveIsTakenOnce()
        {
            var id = Guid.NewGuid();
            PendingSaves.Expect(id, new PendingSave { OfferPublish = true, Message = "причина" });
            var taken = PendingSaves.Take(id);
            Assert.NotNull(taken);
            Assert.Equal("причина", taken.Message);
            Assert.Null(PendingSaves.Take(id));
        }
    }
}
