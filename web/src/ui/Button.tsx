import type { ButtonHTMLAttributes, ReactNode } from 'react'
import { Link, type LinkProps } from 'react-router'
import { cn } from '@/lib/cn'
import { Spinner } from './Spinner'

type Variant = 'primary' | 'subtle' | 'ghost' | 'danger' | 'overlay'
type Size = 'sm' | 'md' | 'lg' | 'icon' | 'icon-sm' | 'icon-lg'

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-accent text-on-accent hover:bg-accent-strong font-semibold',
  subtle: 'bg-elevated text-fg hover:bg-hover',
  ghost: 'bg-transparent text-muted hover:text-fg hover:bg-hover',
  danger: 'bg-transparent text-danger hover:bg-danger/10 ring-1 ring-inset ring-danger/30',
  // Sits on artwork: a translucent fill that stays legible over any backdrop without a blur.
  overlay: 'bg-white/12 text-fg hover:bg-white/20 font-medium',
}

const SIZES: Record<Size, string> = {
  sm: 'h-8 px-3 text-meta gap-1.5',
  md: 'h-10 px-4 text-card gap-2',
  lg: 'h-12 px-6 text-body gap-2.5',
  icon: 'size-10',
  'icon-sm': 'size-8',
  'icon-lg': 'size-12',
}

/** The classes of a button, for the rare element that must look like one without being one. */
export function buttonClass(variant: Variant = 'primary', size: Size = 'md', className?: string): string {
  return cn(
    'inline-flex shrink-0 items-center justify-center rounded-control select-none',
    'transition-[background-color,color,transform] duration-150 ease-out-quint active:scale-[0.97]',
    'disabled:cursor-not-allowed disabled:opacity-50 disabled:active:scale-100',
    VARIANTS[variant],
    SIZES[size],
    className,
  )
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
  size?: Size
  loading?: boolean
  icon?: ReactNode
}

export function Button({
  variant = 'primary',
  size = 'md',
  loading = false,
  icon,
  className,
  children,
  disabled,
  ...rest
}: ButtonProps) {
  return (
    <button
      className={buttonClass(variant, size, className)}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading ? <Spinner className="size-4" /> : icon}
      {children}
    </button>
  )
}

interface ButtonLinkProps extends LinkProps {
  variant?: Variant
  size?: Size
  icon?: ReactNode
}

/**
 * A navigation that looks like a button. A `<Button>` wrapped in a `<Link>` is an interactive element
 * nested in another — invalid HTML that screen readers announce twice — so navigation gets its own.
 */
export function ButtonLink({ variant = 'primary', size = 'md', icon, className, children, ...rest }: ButtonLinkProps) {
  return (
    <Link className={buttonClass(variant, size, typeof className === 'string' ? className : undefined)} {...rest}>
      {icon}
      {children}
    </Link>
  )
}
