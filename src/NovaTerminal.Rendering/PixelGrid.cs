namespace NovaTerminal.Core
{
    public readonly struct PixelGrid
    {
        public int OriginXPx { get; }
        public int OriginYPx { get; }
        public int CellWidthPx { get; }
        public int CellHeightPx { get; }
        public int BaselineOffsetPx { get; }
        public int UnderlineOffsetPx { get; }
        public int StrikeOffsetPx { get; }

        public PixelGrid(
            int originXPx,
            int originYPx,
            int cellWidthPx,
            int cellHeightPx,
            int baselineOffsetPx,
            int underlineOffsetPx,
            int strikeOffsetPx)
        {
            OriginXPx = originXPx;
            OriginYPx = originYPx;
            CellWidthPx = cellWidthPx;
            CellHeightPx = cellHeightPx;
            BaselineOffsetPx = baselineOffsetPx;
            UnderlineOffsetPx = underlineOffsetPx;
            StrikeOffsetPx = strikeOffsetPx;
        }

        public int XForCol(int col)
            => OriginXPx + (col * CellWidthPx);

        public int YForRowTop(int row)
            => OriginYPx + (row * CellHeightPx);

        public int YForBaseline(int row)
            => YForRowTop(row) + BaselineOffsetPx;

        public int YForUnderline(int row)
        {
            int rowTop = YForRowTop(row);
            int y = rowTop + UnderlineOffsetPx;
            int rowBottom = rowTop + CellHeightPx;
            return y > rowBottom ? rowBottom : y;
        }

        public int YForStrike(int row)
            => YForRowTop(row) + StrikeOffsetPx;
    }
}
