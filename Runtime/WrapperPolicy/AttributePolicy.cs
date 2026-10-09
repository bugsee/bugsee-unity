using System;

namespace Bugsee.WrapperPolicy
{
    public struct AttributeDecision
    {
        public bool Accepted { get; }
        public string Error { get; }

        public AttributeDecision(bool accepted, string error)
        {
            Accepted = accepted;
            Error = error;
        }
    }

    public static class AttributePolicy
    {
        const double MaxMagnitude = 9223372036854775808d;
        const decimal MaxDecimalMagnitude = 9223372036854775808m;
        const int MaxStringLength = 1024;

        public static AttributeDecision Evaluate(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                return new AttributeDecision(false, "attribute name must be non-empty");

            switch (value)
            {
                case null:
                    return TypeError(key);
                case string s:
                    if (s.Length > MaxStringLength)
                        return new AttributeDecision(false, "attribute '" + key + "' exceeds 1024 UTF-16 units");
                    return Accept();
                case bool _:
                    return Accept();
                case float f:
                    return EvaluateFloating(key, f);
                case double d:
                    return EvaluateFloating(key, d);
            }

            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                    return Accept();
                case TypeCode.UInt64:
                    return (ulong)value <= (ulong)long.MaxValue ? Accept() : MagnitudeError(key);
                case TypeCode.Decimal:
                    return EvaluateDecimal(key, (decimal)value);
                default:
                    return TypeError(key);
            }
        }

        static AttributeDecision Accept() => new AttributeDecision(true, null);

        static AttributeDecision TypeError(string key) =>
            new AttributeDecision(false, "attribute '" + key + "' must be string, bool, or number");

        static AttributeDecision MagnitudeError(string key) =>
            new AttributeDecision(false, "attribute '" + key + "' exceeds 9223372036854775808");

        static AttributeDecision EvaluateDecimal(string key, decimal value)
        {
            if (value >= MaxDecimalMagnitude || value <= -MaxDecimalMagnitude)
                return MagnitudeError(key);
            return Accept();
        }

        static AttributeDecision EvaluateFloating(string key, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return MagnitudeError(key);
            return EvaluateFloating(key, (double)value);
        }

        static AttributeDecision EvaluateFloating(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return MagnitudeError(key);
            if (value >= MaxMagnitude || value <= -MaxMagnitude)
                return MagnitudeError(key);
            return Accept();
        }
    }
}
