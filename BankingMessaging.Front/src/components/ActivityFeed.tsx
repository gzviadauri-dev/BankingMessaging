import { useMemo } from 'react'
import { CheckCircle2, XCircle, Clock, ArrowDownUp } from 'lucide-react'
import { formatDistanceToNow } from 'date-fns'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { formatAmount } from '@/lib/utils'
import type { Transfer } from '@/types'

interface ActivityEvent {
  id: string
  type: 'completed' | 'failed' | 'debited' | 'pending'
  message: string
  timestamp: string
}

function deriveEvents(transfers: Transfer[]): ActivityEvent[] {
  const events: ActivityEvent[] = []

  for (const t of transfers) {
    const route = `${t.fromAccountId} → ${t.toAccountId}`
    const amt = formatAmount(t.amount, t.currency)

    if (t.status === 'Completed' && t.completedAt) {
      const ms = new Date(t.completedAt).getTime() - new Date(t.createdAt).getTime()
      const duration = ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`
      events.push({
        id: `${t.transferId}-completed`,
        type: 'completed',
        message: `${route} · ${amt} completed in ${duration}`,
        timestamp: t.completedAt,
      })
    } else if (t.status === 'Failed') {
      events.push({
        id: `${t.transferId}-failed`,
        type: 'failed',
        message: `${route} · ${amt} failed${t.failureReason ? `: ${t.failureReason}` : ''}`,
        timestamp: t.completedAt ?? t.createdAt,
      })
    } else if (t.status === 'Debited') {
      events.push({
        id: `${t.transferId}-debited`,
        type: 'debited',
        message: `${route} · ${amt} debited — awaiting credit`,
        timestamp: t.createdAt,
      })
    } else {
      events.push({
        id: `${t.transferId}-pending`,
        type: 'pending',
        message: `${route} · ${amt} queued for processing`,
        timestamp: t.createdAt,
      })
    }
  }

  return events
    .sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())
    .slice(0, 20)
}

const iconMap = {
  completed: <CheckCircle2 className="h-3.5 w-3.5 text-accent flex-shrink-0 mt-0.5" aria-hidden />,
  failed: <XCircle className="h-3.5 w-3.5 text-danger flex-shrink-0 mt-0.5" aria-hidden />,
  debited: <ArrowDownUp className="h-3.5 w-3.5 text-blue-400 flex-shrink-0 mt-0.5" aria-hidden />,
  pending: <Clock className="h-3.5 w-3.5 text-warning flex-shrink-0 mt-0.5" aria-hidden />,
}

const colorMap = {
  completed: 'text-primary/80',
  failed: 'text-danger/80',
  debited: 'text-blue-300/80',
  pending: 'text-muted',
}

interface ActivityFeedProps {
  transfers: Transfer[]
  loading?: boolean
}

export function ActivityFeed({ transfers, loading }: ActivityFeedProps) {
  const events = useMemo(() => deriveEvents(transfers), [transfers])

  if (loading) {
    return (
      <Card className="h-full flex flex-col">
        <CardHeader>
          <Skeleton className="h-4 w-28" />
        </CardHeader>
        <CardContent className="space-y-3">
          {Array.from({ length: 6 }).map((_, i) => (
            <div key={i} className="flex gap-2">
              <Skeleton className="h-3.5 w-3.5 rounded-full flex-shrink-0 mt-0.5" />
              <div className="flex-1 space-y-1">
                <Skeleton className="h-3 w-full" />
                <Skeleton className="h-2.5 w-16" />
              </div>
            </div>
          ))}
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="h-full flex flex-col">
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <CardTitle>Activity Feed</CardTitle>
          <span className="text-[10px] text-muted/50">Polls every 2s</span>
        </div>
      </CardHeader>
      <CardContent className="flex-1 overflow-y-auto p-0">
        {events.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-32 text-center px-4">
            <p className="text-xs text-muted/60">No activity yet.</p>
          </div>
        ) : (
          <div className="divide-y divide-border-dim/30">
            {events.map(event => (
              <div
                key={event.id}
                className="flex gap-2.5 px-4 py-2.5 animate-slide-in-right hover:bg-elevated/30 transition-colors duration-150"
                role="listitem"
              >
                {iconMap[event.type]}
                <div className="flex-1 min-w-0">
                  <p className={`text-xs leading-snug break-words ${colorMap[event.type]}`}>
                    {event.message}
                  </p>
                  <p className="text-[10px] text-muted/40 mt-0.5">
                    {formatDistanceToNow(new Date(event.timestamp), { addSuffix: true })}
                  </p>
                </div>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
