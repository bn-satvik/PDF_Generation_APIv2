using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using System;
using System.Collections.Generic;
using System.Drawing; // For image dimension calculation
using System.IO;
using System.Linq;

namespace Proj.Utils
{
    public static class PdfGenerator
    {
        public static byte[] Generate(Stream imageStream, List<List<string>> tableData, PdfHeaderModel headerModel, PdfFooterModel footerModel)
        {
            if (tableData == null || tableData.Count < 2)
                throw new ArgumentException("Table data must have at least one header row and one data row.");

            var imagePath = Path.GetTempFileName();
            File.WriteAllBytes(imagePath, ReadFully(imageStream));

            var headerRow = tableData[0];
            var dataRows = tableData.Skip(1).ToList();
            var document = new Document();

            // === PAGE 1: IMAGE PAGE ===
            var imageSection = document.AddSection();

            // Dynamically adjust page size based on image ratio
            using (var img = System.Drawing.Image.FromFile(imagePath))
            {
                double dpiX = img.HorizontalResolution;
                double dpiY = img.VerticalResolution;

                // Convert pixels to centimeters
                double widthCm = img.Width / dpiX * 3;
                double heightCm = img.Height / dpiY * 3.5;

                // Optional: clamp max height to avoid oversized pages
                widthCm = Math.Max(widthCm, 15.0);
                heightCm = Math.Max(heightCm, 15.0); // A4 max height


                imageSection.PageSetup.PageWidth = Unit.FromCentimeter(widthCm);
                imageSection.PageSetup.PageHeight = Unit.FromCentimeter(heightCm);
            }


            PdfHeaderLayout.BuildHeader(imageSection, headerModel);
            PdfFooterLayout.BuildFooter(footerModel, imageSection);

            var imageParagraph = imageSection.AddParagraph();
            imageParagraph.Format.SpaceBefore = "2cm";
            imageParagraph.Format.Alignment = ParagraphAlignment.Center;

            var image = imageParagraph.AddImage(imagePath);
            image.Width = "18cm";
            image.LockAspectRatio = true;

            // === PAGE 2+: TABLE PAGE ===
            var tableSection = document.AddSection();

            int columnCount = headerRow.Count;
            double charWidthCm = 0.225;
            double minColWidthCm = 2;
            double maxColWidthCm = 10;

            var columnWidthsCm = new double[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                columnWidthsCm[i] = GetPercentileColumnWidth(headerRow, dataRows, i, 0.9, charWidthCm, minColWidthCm, maxColWidthCm);
            }

            double tableWidthCm = columnWidthsCm.Sum();
            double totalPageWidth = Math.Max(Math.Min(tableWidthCm + 3.0, 70.0), 21.0);
            double margin = (totalPageWidth - tableWidthCm) / 2;

            tableSection.PageSetup.PageWidth = Unit.FromCentimeter(totalPageWidth);
            tableSection.PageSetup.LeftMargin = Unit.FromCentimeter(margin);
            tableSection.PageSetup.RightMargin = Unit.FromCentimeter(margin);
            tableSection.PageSetup.PageHeight = Unit.FromCentimeter(29.7);

            PdfHeaderLayout.BuildHeader(tableSection, headerModel);
            PdfFooterLayout.BuildFooter(footerModel, tableSection);

            var table = tableSection.AddTable();
            table.Borders.Width = 0;
            table.Format.Font.Size = 10;

            for (int i = 0; i < columnCount; i++)
            {
                var col = table.AddColumn(Unit.FromCentimeter(columnWidthsCm[i]));
                col.Format.Font.Size = 9;
            }

            var headerRowObj = table.AddRow();
            headerRowObj.HeadingFormat = true;
            headerRowObj.Format.Font.Bold = true;
            headerRowObj.Format.Alignment = ParagraphAlignment.Left;

            for (int i = 0; i < columnCount; i++)
            {
                var cell = headerRowObj.Cells[i];
                var para = cell.AddParagraph(InsertSoftBreaks(headerRow[i]));
                para.Format.Alignment = ParagraphAlignment.Left;
                para.Format.Font.Size = 12;
                cell.VerticalAlignment = VerticalAlignment.Center;
                para.Format.SpaceBefore = "0.3cm";
                para.Format.SpaceAfter = "0.3cm";
                cell.Borders.Bottom.Width = 0.5;
            }

            foreach (var row in dataRows)
            {
                var dataRow = table.AddRow();
                for (int i = 0; i < columnCount; i++)
                {
                    var cell = dataRow.Cells[i];
                    var text = i < row.Count ? InsertSoftBreaks(row[i]) : "";
                    var para = cell.AddParagraph(text);
                    para.Format.Alignment = ParagraphAlignment.Left;
                    para.Format.Font.Size = 10;
                    cell.VerticalAlignment = VerticalAlignment.Center;
                    para.Format.SpaceBefore = "0.15cm";
                    para.Format.SpaceAfter = "0.15cm";
                    cell.Borders.Bottom.Width = 0.5;
                    cell.Borders.Bottom.Color = Colors.Gray;
                }
            }

            var pdfRenderer = new PdfDocumentRenderer(true)
            {
                Document = document
            };
            pdfRenderer.RenderDocument();

            using var ms = new MemoryStream();
            pdfRenderer.PdfDocument.Save(ms, false);
            return ms.ToArray();
        }

        private static double GetPercentileColumnWidth(List<string> headerRow, List<List<string>> dataRows, int columnIndex, double percentile, double charWidthCm, double minCm, double maxCm)
        {
            // 1. Get length of the longest word in the header cell
            var header = headerRow[columnIndex];
            int maxWordLengthInHeader = header?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(w => w.Length) ?? 0;

            // 2. Collect lengths of cell contents
            var lengths = new List<int> { header?.Length ?? 0 };
            foreach (var row in dataRows)
            {
                if (columnIndex < row.Count && row[columnIndex] != null)
                    lengths.Add(row[columnIndex].Length);
            }

            // 3. Calculate the percentile length
            lengths.Sort();
            int index = (int)(percentile * lengths.Count);
            int percentileLength = lengths[Math.Min(index, lengths.Count - 1)];

            // 4. Determine base width from percentile length
            double percentileWidth = percentileLength * charWidthCm;

            // 5. Calculate the minimum needed width to fit the header word
            double headerWordWidth = maxWordLengthInHeader * charWidthCm;

            // 6. Return the max between headerWordWidth and percentileWidth, clamped to [minCm, maxCm]
            return Math.Min(Math.Max(Math.Max(headerWordWidth, percentileWidth), minCm), maxCm);
        }


        private static byte[] ReadFully(Stream input)
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
        }

        private static string InsertSoftBreaks(string input, int interval = 20)
        {
            if (string.IsNullOrEmpty(input) || input.Length < interval)
                return input;

            return string.Concat(input.Select((c, i) => (i > 0 && i % interval == 0) ? "\u200B" + c : c.ToString()));
        }
    }
}
