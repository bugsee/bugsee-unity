namespace Bugsee.WrapperPolicy
{
    public static class UserIdentifierPolicy
    {
        public static string ForSet(string value)
        {
            if (value == null || value == "")
                return null;
            return value;
        }

        public static string ForGet(string value)
        {
            if (value == null || value == "")
                return null;
            return value;
        }
    }
}
