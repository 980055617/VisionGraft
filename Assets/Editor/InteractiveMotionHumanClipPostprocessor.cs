using UnityEditor;

public sealed class InteractiveMotionHumanClipPostprocessor : AssetPostprocessor
{
    private const string HumanClipFolder = "Assets/Animations/InteractiveMotion/Human/";

    private void OnPreprocessModel()
    {
        string normalizedPath = assetPath.Replace('\\', '/');
        if (!normalizedPath.StartsWith(HumanClipFolder, System.StringComparison.OrdinalIgnoreCase) ||
            !normalizedPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ModelImporter importer = assetImporter as ModelImporter;
        if (importer == null)
        {
            return;
        }

        importer.importAnimation = true;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
    }
}
