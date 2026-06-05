using NUnit.Framework;
using UnityEngine;

public class PointerClickInputTests
{
    [Test]
    public void ResolveClickPositionReturnsPositionWhenPressedThisFrame()
    {
        bool resolved = PointerClickInput.ResolveClickPosition(true, new Vector2(12f, 34f), out Vector2 position);

        Assert.That(resolved, Is.True);
        Assert.That(position, Is.EqualTo(new Vector2(12f, 34f)));
    }

    [Test]
    public void ResolveClickPositionIgnoresUnpressedPointer()
    {
        bool resolved = PointerClickInput.ResolveClickPosition(false, new Vector2(12f, 34f), out Vector2 position);

        Assert.That(resolved, Is.False);
        Assert.That(position, Is.EqualTo(Vector2.zero));
    }
}
