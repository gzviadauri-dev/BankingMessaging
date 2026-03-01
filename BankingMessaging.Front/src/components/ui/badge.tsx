import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-md px-2 py-0.5 text-xs font-medium transition-colors duration-150',
  {
    variants: {
      variant: {
        default: 'bg-accent/15 text-accent border border-accent/25',
        pending: 'bg-warning/15 text-warning border border-warning/25',
        debited: 'bg-blue-500/15 text-blue-400 border border-blue-500/25',
        completed: 'bg-accent/15 text-accent border border-accent/25',
        failed: 'bg-danger/15 text-danger border border-danger/25',
        muted: 'bg-elevated text-muted border border-border-dim',
      },
    },
    defaultVariants: { variant: 'default' },
  },
)

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

function Badge({ className, variant, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ variant }), className)} {...props} />
}

export { Badge, badgeVariants }
