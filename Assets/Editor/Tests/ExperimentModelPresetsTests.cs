using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// 被験者実験の Model ボタン = 既定1 ⇄ 既定2 の表（2026-10-24、ExperimentModelPresets）。
//   - 実験の 6 本と練習の bundle にすべて組があること
//   - 名前が model_index.txt の、書いた種類の一覧にあること（無いと切り替えたときに既定のモデルへ落ちる）
//   - 置換ありの試行と練習だけが切り替えになること（ExperimentTrialRequest.UsesModelPresetToggle）
public class ExperimentModelPresetsTests
{
    private static readonly ExperimentVideo[] ExperimentVideos =
    {
        ExperimentVideo.Video1, ExperimentVideo.Video2, ExperimentVideo.Video3,
        ExperimentVideo.Video4, ExperimentVideo.Video5, ExperimentVideo.Video6,
        ExperimentVideo.Tutorial,
    };

    [Test]
    public void EveryExperimentVideoAndTheTutorialHavePresets()
    {
        ExperimentBundleCatalog catalog = new ExperimentBundleCatalog();
        foreach (ExperimentVideo video in ExperimentVideos)
        {
            string bundle = catalog.Resolve(video);
            Assert.That(ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, bundle), Is.Not.Empty, $"{video} = {bundle}");
        }
    }

    // 実行時の bundle 名は ExperimentScene に保存された bundleCatalog から来る（コードの既定ではない）。
    // シーンの Inspector だけで名前を替えると、表に無い動画になり Model ボタンが何もしなくなる（2026-10-24 の点検）。
    [Test]
    public void EveryBundleNamedInTheExperimentSceneHasPresets()
    {
        List<string> sceneBundles = ReadSceneBundleNames();
        Assert.That(sceneBundles.Count, Is.EqualTo(7), "Video1〜6 と練習");
        foreach (string bundle in sceneBundles)
        {
            Assert.That(ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, bundle), Is.Not.Empty, bundle);
        }
    }

    // model_index.txt は Generate で作り直すまで古いままのことがある。prefab の実物（と同じ名前）があることも確かめる。
    [Test]
    public void EveryPresetPrefabExistsInResources()
    {
        foreach (ExperimentModelPreset entry in ExperimentModelPresets.Entries)
        {
            foreach (string name in new[] { entry.preset1, entry.preset2 })
            {
                string path = $"Assets/Resources/Models/{entry.category}/{name}.prefab";
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                Assert.That(prefab.name, Is.EqualTo(name), path);
            }
        }
    }

    // 練習の 2/4（Model の切り替え）は、その段を表示している間の切り替えだけ数える（2026-10-24）。1/4 の間に押しても先に済まない。
    [Test]
    public void Tutorial_ChangeModelBeforeStep2_DoesNotSkipTheToggleExplanation()
    {
        ExperimentTutorial tutorial = new ExperimentTutorial(null, ExperimentDisplayMode.ModelReplaced);
        tutorial.RecordOperation("change_model", "via=toggle preset=2 tracks=0:08_Male_A_01|1:39_Lynx now=0|1");
        tutorial.RecordInteraction(1, "random_Static", null);
        tutorial.RecordInteraction(1, "motion_end", "reason=completed");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.ChangeModel), "1/4 の間の押下では 2/4 を済ませない");
        Assert.That(tutorial.Body, Does.Contain("もう一度押すと戻ります"));

        tutorial.RecordOperation("change_model", "via=toggle preset=1 tracks=0:00_Female_A_01|1:36_LabradorDog now=0|1");
        Assert.That(tutorial.CurrentStep, Is.EqualTo(ExperimentTutorial.Step.GrabRotate));
    }

    private static List<string> ReadSceneBundleNames()
    {
        string[] lines = File.ReadAllLines(Path.Combine(Application.dataPath, "Scenes/ExperimentScene.unity"));
        var names = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "videoBundleFileNames:")
            {
                for (int j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal); j++)
                {
                    names.Add(lines[j].Trim().Substring(2).Trim());
                }
            }

            const string tutorialKey = "tutorialBundleFileName:";
            if (lines[i].Trim().StartsWith(tutorialKey, StringComparison.Ordinal))
            {
                names.Add(lines[i].Trim().Substring(tutorialKey.Length).Trim());
            }
        }

        return names;
    }

    [Test]
    public void EveryPresetNameIsInTheModelIndexUnderItsCategory()
    {
        string indexPath = Path.Combine(Application.dataPath, "Resources/Models/model_index.txt");
        HashSet<string> listed = new HashSet<string>(File.ReadAllLines(indexPath), StringComparer.Ordinal);
        foreach (ExperimentModelPreset entry in ExperimentModelPresets.Entries)
        {
            Assert.That(new[] { ExperimentModelPreset.Human, ExperimentModelPreset.Animal, ExperimentModelPreset.Else }, Does.Contain(entry.category));
            foreach (string name in new[] { entry.preset1, entry.preset2 })
            {
                Assert.That(listed.Contains($"Models/{entry.category}/{name}"), Is.True, $"{entry.bundle} track {entry.trackId}: {entry.category}/{name}");
            }
        }
    }

    [Test]
    public void EachTrackAppearsOnceAndItsTwoPresetsDiffer()
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExperimentModelPreset entry in ExperimentModelPresets.Entries)
        {
            Assert.That(seen.Add($"{entry.bundle}#{entry.trackId}"), Is.True, $"{entry.bundle} track {entry.trackId} が 2 回ある");
            Assert.That(entry.preset1, Is.Not.EqualTo(entry.preset2), $"{entry.bundle} track {entry.trackId}");
        }
    }

    // 練習の犬の既定1 は ExperimentScene の tutorialAnimalModelName（ExperimentController が練習の request に入れる名前）と同じにする。
    // 違うと、練習の最初の 1 体と Model で戻ったときの犬が別のモデルになる。
    [Test]
    public void TutorialAnimalPreset1MatchesTheSceneTutorialAnimal()
    {
        string scene = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes/ExperimentScene.unity"));
        const string key = "tutorialAnimalModelName: ";
        int at = scene.IndexOf(key, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        int end = scene.IndexOf('\n', at);
        string sceneValue = scene.Substring(at + key.Length, end - at - key.Length).Trim();

        string tutorialBundle = new ExperimentBundleCatalog().Resolve(ExperimentVideo.Tutorial);
        List<ExperimentModelPreset> tutorial = ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, tutorialBundle);
        ExperimentModelPreset animal = tutorial.Find(e => e.category == ExperimentModelPreset.Animal);
        Assert.That(animal.preset1, Is.EqualTo(sceneValue));
    }

    [Test]
    public void NextFlipsBetweenOneAndTwo()
    {
        Assert.That(ExperimentModelPresets.First, Is.EqualTo(1));
        Assert.That(ExperimentModelPresets.Next(1), Is.EqualTo(2));
        Assert.That(ExperimentModelPresets.Next(2), Is.EqualTo(1));

        ExperimentModelPreset entry = new ExperimentModelPreset("bundle_x.svb", 3, ExperimentModelPreset.Else, "a", "b");
        Assert.That(entry.NameFor(1), Is.EqualTo("a"));
        Assert.That(entry.NameFor(2), Is.EqualTo("b"));
    }

    [Test]
    public void ForBundle_IgnoresCaseAndOtherBundles()
    {
        List<ExperimentModelPreset> car2 = ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, "BUNDLE_CAR2.SVB");
        Assert.That(car2.Count, Is.EqualTo(3));
        Assert.That(ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, "bundle_human.svb"), Is.Empty);
        Assert.That(ExperimentModelPresets.ForBundle(ExperimentModelPresets.Entries, null), Is.Empty);
    }

    [Test]
    public void UsesModelPresetToggle_IsTrueOnlyForModelReplaced()
    {
        ExperimentTrialRequest monocular = new ExperimentTrialRequest(
            "bundle_balloon.svb", ExperimentDisplayMode.Monocular, 0, ExperimentVideo.Video1);
        ExperimentTrialRequest stereoOnly = new ExperimentTrialRequest(
            "bundle_balloon.svb", ExperimentDisplayMode.StereoOnly, 6, ExperimentVideo.Video1);
        ExperimentTrialRequest modelReplaced = new ExperimentTrialRequest(
            "bundle_balloon.svb", ExperimentDisplayMode.ModelReplaced, 12, ExperimentVideo.Video1);
        ExperimentTrialRequest tutorialReplaced = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.ModelReplaced, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);
        ExperimentTrialRequest tutorialMonocular = new ExperimentTrialRequest(
            "bundle_tutorial.svb", ExperimentDisplayMode.Monocular, -1, ExperimentVideo.Tutorial, null, "36_LabradorDog", true);

        Assert.That(monocular.UsesModelPresetToggle, Is.False);
        Assert.That(stereoOnly.UsesModelPresetToggle, Is.False);
        Assert.That(modelReplaced.UsesModelPresetToggle, Is.True);
        Assert.That(tutorialReplaced.UsesModelPresetToggle, Is.True);
        Assert.That(tutorialMonocular.UsesModelPresetToggle, Is.False);
    }
}
