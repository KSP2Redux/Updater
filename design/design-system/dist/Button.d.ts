import type { MouseEventHandler, ReactNode } from 'react';
export type ButtonVariant = 'success' | 'danger' | 'neutral';
export type ButtonSize = 'sm' | 'md';
export interface ButtonProps {
    children?: ReactNode;
    /** success = positive actions (refresh, install update); danger = destructive
     * (remove install, uninstall); neutral = everything else. */
    variant?: ButtonVariant;
    size?: ButtonSize;
    disabled?: boolean;
    title?: string;
    onClick?: MouseEventHandler<HTMLButtonElement>;
}
/**
 * Standard launcher action button (the refresh arrow next to the version dropdown,
 * Settings actions, dialog buttons). Monospace label, 8px radius, flat semantic fills.
 */
export declare function Button({ children, variant, size, disabled, title, onClick }: ButtonProps): import("react").JSX.Element;
