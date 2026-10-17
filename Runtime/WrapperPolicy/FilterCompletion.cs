namespace Bugsee.WrapperPolicy
{
    public static class FilterCompletion
    {
        public const int Drop = 0;
        public const int Keep = 1;

        public static int OnThrow => Drop;
    }
}
