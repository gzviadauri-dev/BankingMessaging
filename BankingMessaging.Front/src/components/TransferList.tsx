import { useState } from 'react'
import { ChevronDown, ChevronRight, Copy, Check } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { TransferStatusBadge } from './TransferStatusBadge'
import { cn, copyToClipboard, formatAmount, formatDuration, truncateId } from '@/lib/utils'
import type { Transfer } from '@/types'

interface TransferListProps {
  transfers: Transfer[]
  loading?: boolean
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false)
  const handleCopy = async () => {
    await copyToClipboard(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }
  return (
    <button
      onClick={handleCopy}
      className="ml-1 inline-flex items-center text-muted/50 hover:text-accent transition-colors duration-150"
      aria-label={copied ? 'Copied!' : `Copy ${label}`}
      title={copied ? 'Copied!' : `Copy ${label}`}
    >
      {copied ? <Check className="h-3 w-3" /> : <Copy className="h-3 w-3" />}
    </button>
  )
}

function TransferRow({ transfer }: { transfer: Transfer }) {
  const [expanded, setExpanded] = useState(false)

  return (
    <>
      <tr
        className={cn(
          'border-b border-border-dim/50 transition-colors duration-150 cursor-pointer animate-fade-up',
          'hover:bg-elevated/50',
          expanded && 'bg-elevated/30',
        )}
        onClick={() => setExpanded(v => !v)}
        aria-expanded={expanded}
        role="button"
        tabIndex={0}
        onKeyDown={e => (e.key === 'Enter' || e.key === ' ') && setExpanded(v => !v)}
      >
        <td className="px-3 py-2.5 text-xs font-mono text-muted/80">
          <div className="flex items-center gap-1">
            {expanded ? (
              <ChevronDown className="h-3 w-3 text-muted/40" />
            ) : (
              <ChevronRight className="h-3 w-3 text-muted/40" />
            )}
            {truncateId(transfer.transferId)}
            <CopyButton text={transfer.transferId} label="transfer ID" />
          </div>
        </td>
        <td className="px-3 py-2.5 text-xs text-muted">
          <span className="text-primary/80 font-medium">{transfer.fromAccountId}</span>
          <span className="text-muted/50 mx-1.5">→</span>
          <span className="text-primary/80 font-medium">{transfer.toAccountId}</span>
        </td>
        <td className="px-3 py-2.5 text-xs font-mono text-right text-primary/90">
          {formatAmount(transfer.amount, transfer.currency)}
        </td>
        <td className="px-3 py-2.5">
          <TransferStatusBadge status={transfer.status} />
        </td>
        <td className="px-3 py-2.5 text-xs text-muted/60 font-mono">
          {formatDuration(transfer.createdAt, transfer.completedAt)}
        </td>
        <td className="px-3 py-2.5 text-xs text-muted/50">
          {new Date(transfer.createdAt).toLocaleTimeString()}
        </td>
      </tr>

      {expanded && (
        <tr className="bg-elevated/20 border-b border-border-dim/50">
          <td colSpan={6} className="px-6 py-3">
            <div className="grid grid-cols-2 gap-x-8 gap-y-1.5 text-xs">
              <div className="flex items-center gap-2">
                <span className="text-muted/60 w-28">Transfer ID</span>
                <span className="font-mono text-primary/80">{transfer.transferId}</span>
                <CopyButton text={transfer.transferId} label="transfer ID" />
              </div>
              {transfer.correlationId && (
                <div className="flex items-center gap-2">
                  <span className="text-muted/60 w-28">Correlation ID</span>
                  <span className="font-mono text-primary/80">{transfer.correlationId}</span>
                  <CopyButton text={transfer.correlationId} label="correlation ID" />
                </div>
              )}
              <div className="flex items-center gap-2">
                <span className="text-muted/60 w-28">Created</span>
                <span className="font-mono text-primary/80">
                  {new Date(transfer.createdAt).toISOString()}
                </span>
              </div>
              {transfer.completedAt && (
                <div className="flex items-center gap-2">
                  <span className="text-muted/60 w-28">Completed</span>
                  <span className="font-mono text-primary/80">
                    {new Date(transfer.completedAt).toISOString()}
                  </span>
                </div>
              )}
              {transfer.failureReason && (
                <div className="flex items-center gap-2 col-span-2">
                  <span className="text-muted/60 w-28">Failure reason</span>
                  <span className="text-danger">{transfer.failureReason}</span>
                </div>
              )}
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

export function TransferList({ transfers, loading }: TransferListProps) {
  if (loading) {
    return (
      <Card className="flex-1">
        <CardHeader>
          <Skeleton className="h-4 w-36" />
        </CardHeader>
        <CardContent className="space-y-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-9 w-full" />
          ))}
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="flex-1">
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <CardTitle>Transfer History</CardTitle>
          <span className="text-xs text-muted/60">Polling every 3s</span>
        </div>
      </CardHeader>
      <CardContent className="p-0">
        {transfers.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 text-center">
            <div className="text-4xl mb-3 opacity-20">↕</div>
            <p className="text-sm text-muted">No transfers yet.</p>
            <p className="text-xs text-muted/60 mt-1">Send one above.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full" role="table" aria-label="Transfer history">
              <thead>
                <tr className="border-b border-border-dim text-left">
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium">ID</th>
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium">Route</th>
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium text-right">Amount</th>
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium">Status</th>
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium">Duration</th>
                  <th className="px-3 py-2 text-xs text-muted/60 font-medium">Time</th>
                </tr>
              </thead>
              <tbody>
                {transfers.map(t => (
                  <TransferRow key={t.transferId} transfer={t} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
