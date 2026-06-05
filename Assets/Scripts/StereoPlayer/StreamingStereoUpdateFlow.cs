public static class StreamingStereoUpdateFlow
{
    public readonly struct Decision
    {
        public Decision(bool updateBundlePickerPlacement, bool updateRuntimePlayback)
        {
            this.updateBundlePickerPlacement = updateBundlePickerPlacement;
            this.updateRuntimePlayback = updateRuntimePlayback;
        }

        public readonly bool updateBundlePickerPlacement;
        public readonly bool updateRuntimePlayback;
    }

    public static Decision Resolve(bool bundlePickerActive)
    {
        return bundlePickerActive
            ? new Decision(true, false)
            : new Decision(false, true);
    }
}
