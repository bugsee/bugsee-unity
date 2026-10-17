namespace Bugsee.WrapperPolicy
{
    public static class WrapperLogSourcePolicy
    {
        public const int Unknown = 0;
        public const int StdOut = 1;
        public const int StdErr = 2;
        public const int WebView = 5;
        public const int Custom = 98;

        public static int Resolve(int? source)
        {
            if (!source.HasValue) return Custom;
            switch (source.Value)
            {
                case Unknown:
                case StdOut:
                case StdErr:
                case WebView:
                case Custom:
                    return source.Value;
                default:
                    return Custom;
            }
        }
    }
}
