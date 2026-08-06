import type { ReactNode } from 'react';
import { GlassPanel, Button } from '@ksp2redux/design';

/** The launcher always shows glass panels over dark background art - preview them in-context. */
const DarkBackdrop = ({ children }: { children: ReactNode }) => (
  <div style={{ background: 'linear-gradient(145deg, #101418 0%, #1c1410 100%)', padding: 24, display: 'inline-block' }}>
    {children}
  </div>
);

export const InstallLog = () => (
  <DarkBackdrop>
    <GlassPanel style={{ width: 440 }}>
      <div style={{ fontSize: 12, whiteSpace: 'pre-line', lineHeight: 1.7 }}>
        {`Creating install plan
Downloading redux-0.2.3.1.patch
Download complete.
Applying patch for version: 0.2.3.1 from prepatch
KSP2 Redux Successfully Installed`}
      </div>
    </GlassPanel>
  </DarkBackdrop>
);

export const ArticlePane = () => (
  <DarkBackdrop>
    <GlassPanel style={{ width: 440 }}>
      <div style={{ fontSize: 24, fontWeight: 700, marginBottom: 4 }}>Beta 7 Parts Preview</div>
      <div style={{ fontSize: 12, color: 'var(--krx-text-secondary)', marginBottom: 12 }}>
        Posted 2026-06-30 by the Redux team
      </div>
      <div style={{ fontSize: 12, lineHeight: 1.7 }}>
        Beta 7 brings a first look at the reworked engine plates and structural parts. The team has
        been rebuilding the part colliders from scratch, which should finally put an end to the
        phantom forces on larger stations.
      </div>
      <div style={{ marginTop: 14 }}>
        <Button variant="neutral" size="sm">
          View on blog
        </Button>
      </div>
    </GlassPanel>
  </DarkBackdrop>
);
