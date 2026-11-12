import { MainButton } from '@ksp2redux/design';

export const Install = () => <MainButton variant="install" />;

export const Update = () => <MainButton variant="update" />;

export const Launch = () => <MainButton variant="launch" />;

export const Cancel = () => <MainButton variant="cancel" />;

export const DisabledWithReason = () => (
  <MainButton
    variant="launch"
    disabled
    title="KSP2 installation not detected. Please select a directory containing KSP2 on the settings tab."
  />
);
