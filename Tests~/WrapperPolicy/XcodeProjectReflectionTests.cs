using System;
using System.Linq;
using System.Reflection;
using Bugsee.Editor;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// XcodeProject reaches Unity's PBXProject by reflection, so the compiler no longer checks its names and parameter
    /// types. The stub PBXProject (Tools~/CompileCheck/Editor/UnityEditorXcodeStubs.cs, signatures of Unity 2021.3, 2022.3
    /// and 6000 as checked against their DLLs) stands in for it: a lookup that does not resolve fails here, not in an export.
    /// </summary>
    public class XcodeProjectReflectionTests
    {
        static object DefaultFor(Type t) => t == typeof(string) ? "x" : t == typeof(bool) ? (object)false : null;

        [Test]
        public void Every_public_member_resolves_against_PBXProject()
        {
            var members = typeof(XcodeProject).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).ToArray();
            Assert.That(members.Length, Is.GreaterThanOrEqualTo(15));

            var project = new XcodeProject();
            foreach (var m in members)
            {
                var args = m.GetParameters().Select(p => DefaultFor(p.ParameterType)).ToArray();
                try
                {
                    m.Invoke(m.IsStatic ? null : project, args);
                }
                catch (TargetInvocationException e) when (e.InnerException is MissingMethodException)
                {
                    Assert.Fail($"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) has no matching PBXProject member: {e.InnerException.Message}");
                }
            }
        }
    }
}
