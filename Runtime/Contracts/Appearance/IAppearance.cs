using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Contracts.Appearance
{
    /// <summary>
    /// Appearance customization. On iOS each property maps onto the SDK's BugseeTheme; a property
    /// with no iOS equivalent is ignored and reads back null. Unavailable on iOS:
    /// Report ActionBarTextColor, ActionBarButtonBackgroundClickedColor, SeverityLabelActiveColor;
    /// Feedback ActionBarButtonBackgroundClickedColor, DateTextColor, EmailSkipBackgroundClickedColor,
    /// EmailContinueClickedColor, InputTextHintColor, BottomDelimiterColor, LoadingBarBackgroundColor,
    /// LoadingTextColor, ErrorTextColor, VersionChangedBackgroundColor, VersionChangedTextColor;
    /// and the Notification::Title string.
    /// </summary>
    public interface IAppearance
    {
        IAppearance SetColor(string propertyName, Color32? color);
        Color32? GetColor(string propertyName);
        IAppearance SetString(string propertyName, string value);
        string GetString(string propertyName);
        IReadOnlyDictionary<string, object> ToMap();
    }
}
