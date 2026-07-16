import type {CSSProperties, ReactNode} from 'react';

export interface GlassPanelProps {
    children?: ReactNode;
    /** Extra classes merged onto the panel. */
    className?: string;
    style?: CSSProperties;
}

/**
 * Translucent dark panel over a blurred backdrop - the launcher's primary content
 * surface (install log, news article pane). Sits best on top of imagery or other
 * busy backgrounds; on a flat page it still reads as a card.
 */
export function GlassPanel({children, className, style}: GlassPanelProps) {
    return (
        <div className={['krx-glass-panel', className].filter(Boolean).join(' ')} style={style}>
            {children}
        </div>
    );
}
