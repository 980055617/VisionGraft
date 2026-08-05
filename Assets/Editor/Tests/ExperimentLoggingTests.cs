using System.Globalization;
using System.Threading;
using NUnit.Framework;

public class ExperimentCsvTests
{
    [Test]
    public void BuildRow_JoinsFieldsWithComma()
    {
        Assert.That(ExperimentCsv.BuildRow("a", "b", "c"), Is.EqualTo("a,b,c"));
    }

    [Test]
    public void BuildRow_EmptyInput_ReturnsEmpty()
    {
        Assert.That(ExperimentCsv.BuildRow(), Is.EqualTo(string.Empty));
        Assert.That(ExperimentCsv.BuildRow(null), Is.EqualTo(string.Empty));
    }

    [Test]
    public void BuildRow_NullFieldBecomesEmptyColumn()
    {
        Assert.That(ExperimentCsv.BuildRow("a", null, "c"), Is.EqualTo("a,,c"));
    }

    // 操作ログの detail には prefab 名などが入るため、区切り文字が混ざっても
    // 列がずれないことを保証する。
    [Test]
    public void EscapeField_QuotesValuesContainingComma()
    {
        Assert.That(ExperimentCsv.EscapeField("a,b"), Is.EqualTo("\"a,b\""));
    }

    [Test]
    public void EscapeField_EscapesEmbeddedQuotes()
    {
        Assert.That(ExperimentCsv.EscapeField("say \"hi\""), Is.EqualTo("\"say \"\"hi\"\"\""));
    }

    [Test]
    public void EscapeField_QuotesValuesContainingNewline()
    {
        Assert.That(ExperimentCsv.EscapeField("a\nb"), Is.EqualTo("\"a\nb\""));
        Assert.That(ExperimentCsv.EscapeField("a\r\nb"), Is.EqualTo("\"a\r\nb\""));
    }

    [Test]
    public void EscapeField_PlainValueIsNotQuoted()
    {
        Assert.That(ExperimentCsv.EscapeField("track=3 prefab=00_Dog"), Is.EqualTo("track=3 prefab=00_Dog"));
    }

    [Test]
    public void BuildRow_EscapesEachFieldIndependently()
    {
        Assert.That(
            ExperimentCsv.BuildRow("P01", "a,b", "plain"),
            Is.EqualTo("P01,\"a,b\",plain"));
    }

    // 実験機のロケールが小数点にカンマを使う設定でも CSV が壊れないこと。
    [Test]
    public void Format_UsesInvariantCultureRegardlessOfCurrentCulture()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            Assert.That(ExperimentCsv.Format(1.5f), Is.EqualTo("1.5"));
            Assert.That(ExperimentCsv.Format(1.5d), Is.EqualTo("1.5"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public void Format_BoolIsWrittenAsZeroOrOne()
    {
        Assert.That(ExperimentCsv.Format(true), Is.EqualTo("1"));
        Assert.That(ExperimentCsv.Format(false), Is.EqualTo("0"));
    }

    [Test]
    public void FormatTimestamp_KeepsMilliseconds()
    {
        System.DateTime value = new System.DateTime(2026, 8, 5, 13, 4, 5, 678);

        Assert.That(ExperimentCsv.FormatTimestamp(value), Is.EqualTo("2026-08-05 13:04:05.678"));
    }
}

public class ExperimentLogWriterNamingTests
{
    // 参加者 ID は実験者の手入力なので、パスを壊す文字が混ざり得る。
    [Test]
    public void SanitizeForFileName_ReplacesPathSeparatorsAndSpaces()
    {
        Assert.That(ExperimentLogWriter.SanitizeForFileName("P 01"), Is.EqualTo("P_01"));
        Assert.That(ExperimentLogWriter.SanitizeForFileName("a/b"), Is.EqualTo("a_b"));
    }

    [Test]
    public void SanitizeForFileName_BlankBecomesUnknown()
    {
        Assert.That(ExperimentLogWriter.SanitizeForFileName(null), Is.EqualTo("unknown"));
        Assert.That(ExperimentLogWriter.SanitizeForFileName(string.Empty), Is.EqualTo("unknown"));
    }

    [Test]
    public void SanitizeForFileName_PlainIdIsUnchanged()
    {
        Assert.That(ExperimentLogWriter.SanitizeForFileName("P01"), Is.EqualTo("P01"));
    }

    [Test]
    public void BuildSessionDirectory_IncludesParticipantAndTimestamp()
    {
        string dir = ExperimentLogWriter.BuildSessionDirectory(
            "root", "P03", new System.DateTime(2026, 8, 5, 13, 4, 5));

        Assert.That(dir, Does.Contain("P03_20260805_130405"));
    }
}

public class ExperimentLogSinkTests
{
    private sealed class RecordingSink : IExperimentLogSink
    {
        public int operations;
        public int interactions;
        public int loops;
        public string lastAction;

        public void RecordOperation(string action, string detail)
        {
            operations++;
            lastAction = action;
        }

        public void RecordInteraction(uint trackId, string kind, string detail)
        {
            interactions++;
        }

        public void RecordVideoLoop()
        {
            loops++;
        }
    }

    [TearDown]
    public void TearDown()
    {
        ExperimentLog.Sink = null;
    }

    // 実験を行わない通常シーンでもプレイヤーのフックが安全に呼べること。
    [Test]
    public void Operation_WithoutSink_DoesNotThrow()
    {
        ExperimentLog.Sink = null;

        Assert.That(ExperimentLog.IsActive, Is.False);
        Assert.DoesNotThrow(() => ExperimentLog.Operation("pause"));
        Assert.DoesNotThrow(() => ExperimentLog.Interaction(1, "random_Static"));
        Assert.DoesNotThrow(() => ExperimentLog.VideoLooped());
    }

    [Test]
    public void Operation_WithSink_ForwardsToSink()
    {
        RecordingSink sink = new RecordingSink();
        ExperimentLog.Sink = sink;

        ExperimentLog.Operation("pause");
        ExperimentLog.Interaction(3, "random_Dynamic");
        ExperimentLog.VideoLooped();

        Assert.That(ExperimentLog.IsActive, Is.True);
        Assert.That(sink.operations, Is.EqualTo(1));
        Assert.That(sink.lastAction, Is.EqualTo("pause"));
        Assert.That(sink.interactions, Is.EqualTo(1));
        Assert.That(sink.loops, Is.EqualTo(1));
    }
}
