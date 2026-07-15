import type { ReactNode } from 'react';
import { Button } from '@ksp2redux/design';

/** Launcher buttons sit on dark surfaces - preview them in-context. */
const DarkBackdrop = ({ children }: { children: ReactNode }) => (
  <div style={{ background: '#181818', padding: 20, display: 'inline-block', borderRadius: 4 }}>
    {children}
  </div>
);

export const Success = () => (
  <DarkBackdrop>
    <Button variant="success">Refresh versions</Button>
  </DarkBackdrop>
);

export const Danger = () => (
  <DarkBackdrop>
    <Button variant="danger">Remove install</Button>
  </DarkBackdrop>
);

export const Neutral = () => (
  <DarkBackdrop>
    <Button variant="neutral">Browse...</Button>
  </DarkBackdrop>
);

export const Small = () => (
  <DarkBackdrop>
    <Button variant="success" size="sm">
      Install update
    </Button>
  </DarkBackdrop>
);

export const Disabled = () => (
  <DarkBackdrop>
    <Button variant="success" disabled title="A refresh is already in progress.">
      Refresh versions
    </Button>
  </DarkBackdrop>
);
