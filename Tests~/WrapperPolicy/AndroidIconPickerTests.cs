using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidIconPickerTests
    {
        [Test]
        public void The_highest_density_raster_app_icon_wins()
        {
            var picked = AndroidIconPicker.Pick(new[]
            {
                "/p/res/mipmap-mdpi/app_icon.png",
                "/p/res/mipmap-xxhdpi/app_icon.png",
                "/p/res/mipmap-hdpi/app_icon.png",
                "/p/res/mipmap-anydpi-v26/app_icon.xml",
                "/p/res/mipmap-xxxhdpi/other.png",
            });

            Assert.That(picked, Is.EqualTo("/p/res/mipmap-xxhdpi/app_icon.png"));
        }

        [Test]
        public void Adaptive_xml_icons_and_other_names_are_not_sent()
        {
            Assert.That(AndroidIconPicker.Pick(new[] { "/p/res/mipmap-anydpi-v26/app_icon.xml", "/p/res/drawable/logo.png" }), Is.Null);
            Assert.That(AndroidIconPicker.Pick(new string[0]), Is.Null);
        }

        [Test]
        public void An_unqualified_folder_ranks_below_any_density_and_windows_paths_work()
        {
            var picked = AndroidIconPicker.Pick(new[] { "C:\\p\\res\\mipmap\\app_icon.webp", "C:\\p\\res\\mipmap-hdpi\\app_icon.webp" });

            Assert.That(picked, Is.EqualTo("C:\\p\\res\\mipmap-hdpi\\app_icon.webp"));
        }
    }
}
