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
        const int MaxStringLength = 1024;

        public static AttributeDecision Evaluate(string key, object value)
        {
            switch (value)
            {
                case string s:
                    if (s.Length > MaxStringLength)
                        return new AttributeDecision(false, "attribute '" + key + "' exceeds 1024 UTF-16 units");
                    return new AttributeDecision(true, null);
                case bool _:
                    return new AttributeDecision(true, null);
                case byte _:
                case short _:
                case int _:
                case long _:
                    return new AttributeDecision(true, null);
                case float f:
                    return EvaluateFloating(key, f);
                case double d:
                    return EvaluateFloating(key, d);
                default:
                    return new AttributeDecision(false, "attribute '" + key + "' must be string, bool, or number");
            }
        }

        static AttributeDecision EvaluateFloating(string key, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return new AttributeDecision(false, "attribute '" + key + "' must be string, bool, or number");
            return EvaluateFloating(key, (double)value);
        }

        static AttributeDecision EvaluateFloating(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return new AttributeDecision(false, "attribute '" + key + "' must be string, bool, or number");
            if (value >= MaxMagnitude || value <= -MaxMagnitude)
                return new AttributeDecision(false, "attribute '" + key + "' exceeds 9223372036854775808");
            return new AttributeDecision(true, null);
        }
    }
}
