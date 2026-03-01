import { useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { useInitiateTransfer } from '@/hooks/useTransfers'
import type { Account } from '@/types'

interface RetrySimulatorProps {
  accounts: Account[]
}

type ChaosType = 'funds' | 'error' | 'race'

export function RetrySimulator({ accounts }: RetrySimulatorProps) {
  const [counts, setCounts] = useState<Record<ChaosType, number>>({ funds: 0, error: 0, race: 0 })
  const { mutateAsync } = useInitiateTransfer()

  const bump = (type: ChaosType) =>
    setCounts(c => ({ ...c, [type]: c[type] + 1 }))

  const getAccounts = () => {
    if (accounts.length < 2) return null
    return { from: accounts[0].accountId, to: accounts[1].accountId }
  }

  const handleInsufficientFunds = async () => {
    const accs = getAccounts()
    if (!accs) { toast.error('Need at least 2 accounts'); return }
    bump('funds')
    try {
      await mutateAsync({ fromAccountId: accs.from, toAccountId: accs.to, amount: 999_999, currency: 'USD' })
      toast.info('Transfer sent — expect failure due to insufficient funds')
    } catch {
      toast.error('Transfer rejected at API level')
    }
  }

  const handleTransientError = async () => {
    const accs = getAccounts()
    if (!accs) { toast.error('Need at least 2 accounts'); return }
    bump('error')
    try {
      await mutateAsync({
        fromAccountId: accs.from,
        toAccountId: accs.to,
        amount: 1,
        currency: 'USD',
        simulateError: true,
      })
      toast.info('Transfer sent with simulateError=true — watch retry attempts in Jaeger')
    } catch {
      toast.error('Transfer failed')
    }
  }

  const handleRaceCondition = async () => {
    const accs = getAccounts()
    if (!accs) { toast.error('Need at least 2 accounts'); return }
    bump('race')
    toast.info('Firing 3 concurrent transfers from the same account…')
    try {
      await Promise.all([
        mutateAsync({ fromAccountId: accs.from, toAccountId: accs.to, amount: 50, currency: 'USD' }),
        mutateAsync({ fromAccountId: accs.from, toAccountId: accs.to, amount: 50, currency: 'USD' }),
        mutateAsync({ fromAccountId: accs.from, toAccountId: accs.to, amount: 50, currency: 'USD' }),
      ])
      toast.success('3 transfers initiated — SQL Server locking prevents double-spend')
    } catch {
      toast.error('One or more race condition transfers failed')
    }
  }

  return (
    <div className="rounded-md border border-border-dim bg-surface p-4 space-y-3">
      <div className="flex items-center gap-2">
        <span className="text-xs" aria-hidden>💀</span>
        <h3 className="text-xs font-semibold text-muted uppercase tracking-widest">Chaos Controls</h3>
      </div>

      <div className="space-y-2">
        <Button
          variant="danger"
          size="sm"
          className="w-full justify-between"
          onClick={handleInsufficientFunds}
          aria-label="Simulate insufficient funds — sends a $999,999 transfer"
        >
          <span>Insufficient Funds</span>
          {counts.funds > 0 && (
            <span className="bg-danger/30 text-danger text-[10px] font-mono px-1.5 py-0.5 rounded">
              ×{counts.funds}
            </span>
          )}
        </Button>

        <Button
          variant="warning"
          size="sm"
          className="w-full justify-between"
          onClick={handleTransientError}
          aria-label="Simulate transient error — triggers retry mechanism"
        >
          <span>Transient Error</span>
          {counts.error > 0 && (
            <span className="bg-warning/30 text-warning text-[10px] font-mono px-1.5 py-0.5 rounded">
              ×{counts.error}
            </span>
          )}
        </Button>

        <Button
          variant="subtle"
          size="sm"
          className="w-full justify-between"
          onClick={handleRaceCondition}
          aria-label="Simulate race condition — fires 3 concurrent transfers"
        >
          <span>Race Condition ×3</span>
          {counts.race > 0 && (
            <span className="bg-elevated text-muted text-[10px] font-mono px-1.5 py-0.5 rounded border border-border-dim">
              ×{counts.race}
            </span>
          )}
        </Button>
      </div>

      <p className="text-[10px] text-muted/50 leading-relaxed">
        Watch Jaeger :16686 for retry spans and SQL locking traces.
      </p>
    </div>
  )
}
