namespace NovaTerminal.Core
{
    public struct SearchMatch
    {
        public int AbsRow;
        public int StartCol;
        public int EndCol;

        public SearchMatch(int row, int start, int end)
        {
            AbsRow = row;
            StartCol = start;
            EndCol = end;
        }
    }
}
