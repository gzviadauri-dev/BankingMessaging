import { useState } from 'react'
import { AreaChart, Area, ResponsiveContainer, Tooltip } from 'recharts'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { cn, formatAmount } from '@/lib/utils'
import type { Account } from '@/types'

function generateSparkline(balance: number) {
  const points = 9
  const variance = balance * 0.08
  return Array.from({ length: points }, (_, i) => ({
    v: Math.max(0, balance + (Math.random() - 0.52) * variance * ((points - i) / points)),
  })).concat([{ v: balance }])
}

interface AccountCardProps {
  account: Account
  onSelect?: (accountId: string) => void
  isSelected?: boolean
}

export function AccountCard({ account, onSelect, isSelected }: AccountCardProps) {
  const [sparkData] = useState(() => generateSparkline(account.balance))

  const balanceClass =
    account.balance > 5000
      ? 'text-accent drop-shadow-[0_0_8px_rgba(0,212,170,0.4)]'
      : account.balance < 1000
        ? 'text-warning'
        : 'text-primary'

  return (
    <button
      onClick={() => onSelect?.(account.accountId)}
      className={cn(
        'w-full text-left rounded-md border transition-all duration-150 cursor-pointer group',
        'bg-surface hover:border-accent/50 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent',
        isSelected ? 'border-accent/60 bg-accent/5' : 'border-border-dim',
      )}
      aria-label={`Account ${account.accountId}, balance ${formatAmount(account.balance, account.currency)}`}
    >
      <div className="p-4 pb-2">
        <div className="flex items-start justify-between mb-1">
          <div>
            <p className="text-xs text-muted font-medium uppercase tracking-widest">
              {account.accountId}
            </p>
            <p className="text-xs text-muted/70 mt-0.5">{account.ownerId}</p>
          </div>
          <span className="text-xs text-muted/60 font-mono">{account.currency}</span>
        </div>
        <p className={cn('text-xl font-mono font-medium mt-2 tracking-tight', balanceClass)}>
          {formatAmount(account.balance, account.currency)}
        </p>
      </div>

      <div className="h-12 px-1">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={sparkData} margin={{ top: 2, right: 0, left: 0, bottom: 0 }}>
            <defs>
              <linearGradient id={`grad-${account.accountId}`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="#00d4aa" stopOpacity={0.3} />
                <stop offset="100%" stopColor="#00d4aa" stopOpacity={0} />
              </linearGradient>
            </defs>
            <Area
              type="monotone"
              dataKey="v"
              stroke="#00d4aa"
              strokeWidth={1.5}
              fill={`url(#grad-${account.accountId})`}
              dot={false}
              isAnimationActive={false}
            />
            <Tooltip
              content={() => null}
              cursor={{ stroke: '#00d4aa', strokeWidth: 1, strokeDasharray: '3 3' }}
            />
          </AreaChart>
        </ResponsiveContainer>
      </div>

      <div className="px-4 pb-3 pt-1">
        <p className="text-[10px] text-muted/50">
          Updated {new Date(account.updatedAt).toLocaleTimeString()}
        </p>
      </div>
    </button>
  )
}

export function AccountCardSkeleton() {
  return (
    <Card>
      <CardContent className="p-4 space-y-3">
        <Skeleton className="h-3 w-24" />
        <Skeleton className="h-6 w-32" />
        <Skeleton className="h-12 w-full" />
      </CardContent>
    </Card>
  )
}
