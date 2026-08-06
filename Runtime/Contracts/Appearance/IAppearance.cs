using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Contracts.Appearance
{
    public interface IAppearance
    {
        IAppearance SetColor(string propertyName, Color32? color);
        Color32? GetColor(string propertyName);
        IAppearance SetString(string propertyName, string value);
        string GetString(string propertyName);
        IReadOnlyDictionary<string, object> ToMap();
    }
}
