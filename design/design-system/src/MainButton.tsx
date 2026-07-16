import type {MouseEventHandler} from 'react';

export type MainButtonVariant = 'install' | 'update' | 'launch' | 'cancel';

export interface MainButtonProps {
    /** Which of the launcher's four LED-art states to show. Also the default label. */
    variant: MainButtonVariant;
    /** Overrides the uppercase variant name drawn on the art. */
    label?: string;
    disabled?: boolean;
    /** Shown while hovering a disabled button - the launcher uses this to explain WHY it's disabled. */
    title?: string;
    onClick?: MouseEventHandler<HTMLButtonElement>;
}

/**
 * The launcher's big LED-style sidebar action button (INSTALL / UPDATE / LAUNCH /
 * CANCEL). Fixed 260x76 art with the label drawn in the Led Counter 7 face; hover
 * and press tint the art, disabled dims it behind a strong scrim.
 */
export function MainButton({variant, label, disabled, title, onClick}: MainButtonProps) {
    return (
        <button
            type="button"
            className={`krx-main-button krx-main-button--${variant}`}
            disabled={disabled}
            title={title}
            onClick={onClick}
        >
            <span className="krx-main-button__label">{(label ?? variant).toUpperCase()}</span>
            <span className="krx-main-button__tint"/>
        </button>
    );
}
