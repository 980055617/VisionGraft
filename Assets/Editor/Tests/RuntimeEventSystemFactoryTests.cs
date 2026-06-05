using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

public class RuntimeEventSystemFactoryTests
{
    [Test]
    public void EnsureCreatesEventSystemWithInputModuleWhenMissing()
    {
        EventSystem eventSystem = RuntimeEventSystemFactory.Ensure(null);
        try
        {
            Assert.That(eventSystem, Is.Not.Null);
            Assert.That(eventSystem.gameObject.name, Is.EqualTo("EventSystem"));
            Assert.That(eventSystem.GetComponent<InputSystemUIInputModule>(), Is.Not.Null);
        }
        finally
        {
            Object.DestroyImmediate(eventSystem.gameObject);
        }
    }

    [Test]
    public void EnsureReusesExistingEventSystemAndAddsMissingInputModule()
    {
        GameObject existing = new GameObject("ExistingEventSystem");
        EventSystem eventSystem = existing.AddComponent<EventSystem>();
        try
        {
            EventSystem result = RuntimeEventSystemFactory.Ensure(eventSystem);

            Assert.That(result, Is.EqualTo(eventSystem));
            Assert.That(result.gameObject.name, Is.EqualTo("ExistingEventSystem"));
            Assert.That(result.GetComponent<InputSystemUIInputModule>(), Is.Not.Null);
        }
        finally
        {
            Object.DestroyImmediate(existing);
        }
    }
}
