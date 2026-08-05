using NUnit.Framework;

namespace Hidano.AvatarSetupTool.Editor.Tests
{
    /// <summary>
    /// テスト基盤のプレースホルダ。本体アセンブリの internal メンバが
    /// InternalsVisibleTo 経由でテストアセンブリから参照・実行できることを確認する。
    /// </summary>
    public class TestAssemblySetupTests
    {
        [Test]
        public void InternalMember_IsAccessibleFromTestAssembly()
        {
            var pattern = ModelCaptureService.EffectivePattern(
                "<Name>", multipleTargets: true, forStill: true, bothViews: true);

            Assert.That(pattern, Is.EqualTo("<Target>_<Name>_<View>_<Direction>"));
        }
    }
}
