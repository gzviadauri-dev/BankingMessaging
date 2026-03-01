import { useState } from 'react'
import { Loader2, Send } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useInitiateTransfer } from '@/hooks/useTransfers'
import { truncateId } from '@/lib/utils'
import type { Account } from '@/types'

interface TransferFormProps {
  accounts: Account[]
  loading?: boolean
  prefilledFrom?: string
  onPrefilledConsumed?: () => void
}

export function TransferForm({ accounts, loading, prefilledFrom, onPrefilledConsumed }: TransferFormProps) {
  const [fromId, setFromId] = useState(prefilledFrom ?? '')
  const [toId, setToId] = useState('')
  const [amount, setAmount] = useState('')
  const [simulateError, setSimulateError] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)

  const { mutateAsync, isPending } = useInitiateTransfer()

  // Consume the prefilled value when it changes
  if (prefilledFrom && prefilledFrom !== fromId) {
    setFromId(prefilledFrom)
    onPrefilledConsumed?.()
  }

  const validate = () => {
    if (!fromId) return 'Select a source account'
    if (!toId) return 'Select a destination account'
    if (fromId === toId) return 'Source and destination must differ'
    const amt = parseFloat(amount)
    if (isNaN(amt) || amt <= 0) return 'Amount must be greater than $0'
    return null
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const err = validate()
    if (err) { setFormError(err); return }
    setFormError(null)

    try {
      const result = await mutateAsync({
        fromAccountId: fromId,
        toAccountId: toId,
        amount: parseFloat(amount),
        currency: 'USD',
        simulateError,
      })
      toast.success('Transfer initiated', {
        description: `ID: ${truncateId(result.transferId)}`,
      })
      setAmount('')
      setSimulateError(false)
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Transfer failed'
      setFormError(msg)
      toast.error('Transfer failed', { description: msg })
    }
  }

  const fromOptions = accounts
  const toOptions = accounts.filter(a => a.accountId !== fromId)

  if (loading) {
    return (
      <Card>
        <CardHeader><Skeleton className="h-4 w-32" /></CardHeader>
        <CardContent className="space-y-3">
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-9 w-24" />
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Initiate Transfer</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="space-y-3" noValidate>
          {/* From Account */}
          <div className="space-y-1">
            <label htmlFor="from-account" className="text-xs text-muted font-medium">
              From Account
            </label>
            <select
              id="from-account"
              value={fromId}
              onChange={e => setFromId(e.target.value)}
              className="flex h-9 w-full rounded-md border border-border-dim bg-elevated px-3 py-2 text-sm text-primary transition-colors duration-150 focus:outline-none focus:ring-1 focus:ring-accent focus:border-accent"
              aria-label="Source account"
            >
              <option value="" className="bg-surface">Select account…</option>
              {fromOptions.map(a => (
                <option key={a.accountId} value={a.accountId} className="bg-surface">
                  {a.accountId} — {a.ownerId}
                </option>
              ))}
            </select>
          </div>

          {/* To Account */}
          <div className="space-y-1">
            <label htmlFor="to-account" className="text-xs text-muted font-medium">
              To Account
            </label>
            <select
              id="to-account"
              value={toId}
              onChange={e => setToId(e.target.value)}
              className="flex h-9 w-full rounded-md border border-border-dim bg-elevated px-3 py-2 text-sm text-primary transition-colors duration-150 focus:outline-none focus:ring-1 focus:ring-accent focus:border-accent"
              aria-label="Destination account"
            >
              <option value="" className="bg-surface">Select account…</option>
              {toOptions.map(a => (
                <option key={a.accountId} value={a.accountId} className="bg-surface">
                  {a.accountId} — {a.ownerId}
                </option>
              ))}
            </select>
          </div>

          {/* Amount */}
          <div className="space-y-1">
            <label htmlFor="amount" className="text-xs text-muted font-medium">
              Amount
            </label>
            <div className="relative">
              <span className="absolute left-3 top-1/2 -translate-y-1/2 text-muted text-sm font-mono">$</span>
              <Input
                id="amount"
                type="number"
                min="0.01"
                step="0.01"
                value={amount}
                onChange={e => setAmount(e.target.value)}
                placeholder="0.00"
                className="pl-7 pr-16 font-mono text-right"
                aria-label="Transfer amount in USD"
              />
              <span className="absolute right-3 top-1/2 -translate-y-1/2 text-muted text-xs font-medium">USD</span>
            </div>
          </div>

          {/* Simulate error toggle */}
          <div className="flex items-center gap-3 pt-1">
            <button
              type="button"
              role="switch"
              aria-checked={simulateError}
              aria-label="Simulate transient error"
              onClick={() => setSimulateError(v => !v)}
              className={`relative h-5 w-9 rounded-full transition-colors duration-150 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent ${
                simulateError ? 'bg-warning/60' : 'bg-elevated border border-border-dim'
              }`}
            >
              <span
                className={`absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-surface border border-border-dim transition-transform duration-150 ${
                  simulateError ? 'translate-x-4 bg-warning' : ''
                }`}
              />
            </button>
            <span className="text-xs text-muted">Simulate transient error</span>
          </div>

          {/* Error message */}
          {formError && (
            <p className="text-xs text-danger flex items-center gap-1.5" role="alert">
              <span className="h-1.5 w-1.5 rounded-full bg-danger inline-block" aria-hidden />
              {formError}
            </p>
          )}

          {/* Submit */}
          <Button
            type="submit"
            className="w-full"
            disabled={isPending}
            aria-label={isPending ? 'Sending transfer…' : 'Send transfer'}
          >
            {isPending ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                Sending…
              </>
            ) : (
              <>
                <Send className="h-4 w-4" aria-hidden />
                Send Transfer
              </>
            )}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
