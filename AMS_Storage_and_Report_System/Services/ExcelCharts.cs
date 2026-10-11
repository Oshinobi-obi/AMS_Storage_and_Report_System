// ExcelCharts.cs — adds real Excel charts (bar, column and pie) to a workbook made with ClosedXML.
// ClosedXML can't draw charts, so the saved file is opened again with the Open XML SDK
// (installed together with ClosedXML) and the chart parts are written in.
// The charts point at cell ranges, so they update when someone edits the numbers in Excel.

using System.Globalization;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AMS_Storage_and_Report_System.Services;

public enum ExcelChartKind { Bar, Column, Pie }

/// One data series: its name, the cells that hold its values, the values themselves
/// (kept in the file so previews show the chart before Excel recalculates), and a colour.
public sealed record ExcelSeries(string Name, string ValuesRange, IReadOnlyList<double> Values, string ColorHex);

/// A chart placed over the cells from (FromCol, FromRow) to (ToCol, ToRow); 0-based, like Excel's drawing grid.
public sealed record ExcelChart(
    string Sheet, ExcelChartKind Kind, string Title,
    string CategoriesRange, IReadOnlyList<string> Categories, IReadOnlyList<ExcelSeries> Series,
    int FromCol, int FromRow, int ToCol, int ToRow,
    IReadOnlyList<string>? PointColors = null,   // pie slices, or one colour per bar
    string NumberFormat = "#,##0");

public static class ExcelCharts
{
    private const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    /// Returns a copy of the workbook with the charts added.
    public static byte[] Add(byte[] workbook, IEnumerable<ExcelChart> charts)
    {
        using var ms = new MemoryStream();
        ms.Write(workbook);
        ms.Position = 0;
        using (var doc = SpreadsheetDocument.Open(ms, true))
        {
            var wbPart = doc.WorkbookPart!;
            var axisId = 50000u;
            foreach (var group in charts.GroupBy(c => c.Sheet))
            {
                var sheet = wbPart.Workbook.Descendants<Sheet>().First(s => s.Name == group.Key);
                var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
                var drawingsPart = wsPart.AddNewPart<DrawingsPart>();

                var anchors = new StringBuilder();
                var n = 0;
                foreach (var chart in group)
                {
                    n++;
                    var chartPart = drawingsPart.AddNewPart<ChartPart>();
                    using (var s = chartPart.GetStream(FileMode.Create))
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                        w.Write(ChartXml(chart, axisId += 2));
                    anchors.Append(AnchorXml(chart, n, drawingsPart.GetIdOfPart(chartPart)));
                }
                using (var s = drawingsPart.GetStream(FileMode.Create))
                using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                    w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><xdr:wsDr xmlns:xdr=\"{Xdr}\" xmlns:a=\"{A}\">{anchors}</xdr:wsDr>");

                // <drawing> must come before these elements in the sheet XML.
                var ws = wsPart.Worksheet;
                var drawing = new Drawing { Id = wsPart.GetIdOfPart(drawingsPart) };
                var before = ws.ChildElements.FirstOrDefault(e => e is LegacyDrawing or LegacyDrawingHeaderFooter or Picture
                    or OleObjects or Controls or WebPublishItems or TableParts or WorksheetExtensionList);
                if (before is not null) ws.InsertBefore(drawing, before); else ws.Append(drawing);
                ws.Save();
            }
        }
        return ms.ToArray();
    }

    private static string AnchorXml(ExcelChart c, int n, string relId) => $"""
        <xdr:twoCellAnchor editAs="oneCell">
          <xdr:from><xdr:col>{c.FromCol}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{c.FromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>
          <xdr:to><xdr:col>{c.ToCol}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{c.ToRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>
          <xdr:graphicFrame macro="">
            <xdr:nvGraphicFramePr><xdr:cNvPr id="{n + 1}" name="Chart {n}" descr="{X(c.Title)}"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>
            <xdr:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></xdr:xfrm>
            <a:graphic><a:graphicData uri="{C}"><c:chart xmlns:c="{C}" xmlns:r="{R}" r:id="{relId}"/></a:graphicData></a:graphic>
          </xdr:graphicFrame>
          <xdr:clientData/>
        </xdr:twoCellAnchor>
        """;

    private static string ChartXml(ExcelChart c, uint axisId)
    {
        var sb = new StringBuilder();
        sb.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><c:chartSpace xmlns:c=\"{C}\" xmlns:a=\"{A}\" xmlns:r=\"{R}\">");
        sb.Append("<c:roundedCorners val=\"0\"/><c:chart>");
        sb.Append($"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1300\" b=\"1\"/></a:pPr><a:r><a:rPr lang=\"en-US\" sz=\"1300\" b=\"1\"><a:solidFill><a:srgbClr val=\"141A3A\"/></a:solidFill></a:rPr><a:t>{X(c.Title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
        sb.Append("<c:autoTitleDeleted val=\"0\"/><c:plotArea><c:layout/>");

        if (c.Kind == ExcelChartKind.Pie)
        {
            sb.Append("<c:pieChart><c:varyColors val=\"1\"/>");
            AppendSeries(sb, c, 0, c.Series[0], pie: true);
            sb.Append("<c:firstSliceAng val=\"0\"/></c:pieChart></c:plotArea>");
            sb.Append("<c:legend><c:legendPos val=\"r\"/><c:overlay val=\"0\"/><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1000\"/></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr></c:legend>");
        }
        else
        {
            var horizontal = c.Kind == ExcelChartKind.Bar;
            sb.Append($"<c:barChart><c:barDir val=\"{(horizontal ? "bar" : "col")}\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>");
            for (var i = 0; i < c.Series.Count; i++) AppendSeries(sb, c, i, c.Series[i], pie: false);
            sb.Append($"<c:gapWidth val=\"60\"/><c:axId val=\"{axisId}\"/><c:axId val=\"{axisId + 1}\"/></c:barChart>");
            // Category axis. Horizontal bars list the first row at the top (reverse order),
            // so the value axis is moved to the bottom ("crosses at maximum").
            sb.Append($"<c:catAx><c:axId val=\"{axisId}\"/><c:scaling><c:orientation val=\"{(horizontal ? "maxMin" : "minMax")}\"/></c:scaling><c:delete val=\"0\"/>");
            sb.Append($"<c:axPos val=\"{(horizontal ? "l" : "b")}\"/><c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            sb.Append("<c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"BFC5D6\"/></a:solidFill></a:ln></c:spPr>");
            sb.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\"><a:solidFill><a:srgbClr val=\"3B4260\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
            sb.Append($"<c:crossAx val=\"{axisId + 1}\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>");
            sb.Append($"<c:valAx><c:axId val=\"{axisId + 1}\"/><c:scaling><c:orientation val=\"minMax\"/><c:min val=\"0\"/></c:scaling><c:delete val=\"0\"/>");
            sb.Append($"<c:axPos val=\"{(horizontal ? "b" : "l")}\"/>");
            sb.Append("<c:majorGridlines><c:spPr><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"E3E6EF\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>");
            sb.Append($"<c:numFmt formatCode=\"{X(c.NumberFormat)}\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            sb.Append("<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>");
            sb.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\"><a:solidFill><a:srgbClr val=\"5A6280\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
            sb.Append($"<c:crossAx val=\"{axisId}\"/><c:crosses val=\"{(horizontal ? "max" : "autoZero")}\"/><c:crossBetween val=\"between\"/></c:valAx></c:plotArea>");
            if (c.Series.Count > 1)
                sb.Append("<c:legend><c:legendPos val=\"t\"/><c:overlay val=\"0\"/><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"1000\"/></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr></c:legend>");
        }
        sb.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>");
        sb.Append("<c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"D8DCEA\"/></a:solidFill></a:ln></c:spPr>");
        sb.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr><a:latin typeface=\"Calibri\"/></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        sb.Append("</c:chartSpace>");
        return sb.ToString();
    }

    private static void AppendSeries(StringBuilder sb, ExcelChart c, int index, ExcelSeries s, bool pie)
    {
        sb.Append($"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/>");
        sb.Append($"<c:tx><c:v>{X(s.Name)}</c:v></c:tx>");
        sb.Append($"<c:spPr><a:solidFill><a:srgbClr val=\"{s.ColorHex.TrimStart('#')}\"/></a:solidFill>{(pie ? "<a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln>" : "")}</c:spPr>");
        if (!pie) sb.Append("<c:invertIfNegative val=\"0\"/>");
        if (c.PointColors is { Count: > 0 } colors)
        {
            for (var p = 0; p < Math.Min(colors.Count, c.Categories.Count); p++)
            {
                sb.Append($"<c:dPt><c:idx val=\"{p}\"/>{(pie ? "<c:bubble3D val=\"0\"/>" : "<c:invertIfNegative val=\"0\"/><c:bubble3D val=\"0\"/>")}");
                sb.Append($"<c:spPr><a:solidFill><a:srgbClr val=\"{colors[p].TrimStart('#')}\"/></a:solidFill>{(pie ? "<a:ln w=\"19050\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln>" : "")}</c:spPr></c:dPt>");
            }
        }
        // Value labels on the marks (and the share for pie slices)
        sb.Append($"<c:dLbls><c:numFmt formatCode=\"{X(c.NumberFormat)}\" sourceLinked=\"0\"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
        sb.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\" b=\"1\"><a:solidFill><a:srgbClr val=\"141A3A\"/></a:solidFill></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        sb.Append(pie ? "<c:dLblPos val=\"bestFit\"/>" : "<c:dLblPos val=\"outEnd\"/>");
        sb.Append($"<c:showLegendKey val=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/>");
        sb.Append("</c:dLbls>");

        sb.Append($"<c:cat><c:strRef><c:f>{X(c.CategoriesRange)}</c:f><c:strCache><c:ptCount val=\"{c.Categories.Count}\"/>");
        for (var i = 0; i < c.Categories.Count; i++) sb.Append($"<c:pt idx=\"{i}\"><c:v>{X(c.Categories[i])}</c:v></c:pt>");
        sb.Append("</c:strCache></c:strRef></c:cat>");
        sb.Append($"<c:val><c:numRef><c:f>{X(s.ValuesRange)}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{s.Values.Count}\"/>");
        for (var i = 0; i < s.Values.Count; i++) sb.Append($"<c:pt idx=\"{i}\"><c:v>{s.Values[i].ToString(CultureInfo.InvariantCulture)}</c:v></c:pt>");
        sb.Append("</c:numCache></c:numRef></c:val></c:ser>");
    }

    /// 'Sheet name'!$B$5:$B$14
    public static string Range(string sheet, int col, int firstRow, int lastRow) =>
        $"'{sheet.Replace("'", "''")}'!${ColumnLetter(col)}${firstRow}:${ColumnLetter(col)}${lastRow}";

    private static string ColumnLetter(int col)
    {
        var s = "";
        for (; col > 0; col = (col - 1) / 26) s = (char)('A' + (col - 1) % 26) + s;
        return s;
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
}
