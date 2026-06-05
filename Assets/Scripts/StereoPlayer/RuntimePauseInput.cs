public static class RuntimePauseInput
{
    public readonly struct Decision
    {
        public Decision(bool togglePause, bool previousPrimaryButtonPressed)
        {
            this.togglePause = togglePause;
            this.previousPrimaryButtonPressed = previousPrimaryButtonPressed;
        }

        public readonly bool togglePause;
        public readonly bool previousPrimaryButtonPressed;
    }

    public static Decision Resolve(
        bool hotkeyPressed,
        bool hasPrimaryButton,
        bool primaryButtonPressed,
        bool previousPrimaryButtonPressed)
    {
        if (hotkeyPressed)
        {
            return new Decision(true, previousPrimaryButtonPressed);
        }

        if (!hasPrimaryButton)
        {
            return new Decision(false, false);
        }

        bool togglePause = primaryButtonPressed && !previousPrimaryButtonPressed;
        return new Decision(togglePause, primaryButtonPressed);
    }
}
