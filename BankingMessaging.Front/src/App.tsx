import { useState } from 'react'
import { BanknoteIcon, WifiOff } from 'lucide-react'
import { Toaster } from 'sonner'
import { AccountCard, AccountCardSkeleton } from '@/components/AccountCard'
import { TransferForm } from '@/components/TransferForm'
import { TransferList } from '@/components/TransferList'
import { ActivityFeed } from '@/components/ActivityFeed'
import { RetrySimulator } from '@/components/RetrySimulator'
import { useAccounts, useApiHealth } from '@/hooks/useAccounts'
import { useTransfers } from '@/hooks/useTransfers'
import { cn } from '@/lib/utils'

function Header({ apiOnline }: { apiOnline: boolean }) {
  return (
    <header className="h-14 flex items-center justify-between px-5 border-b border-border-dim bg-surface/80 backdrop-blur-sm sticky top-0 z-10">
      <div className="flex items-center gap-3">
        <BanknoteIcon size={20} className="text-accent" aria-hidden />
        <span className="font-semibold text-primary text-sm tracking-tight">
          Banking Messaging Lab
        </span>
        <span className="hidden sm:inline text-xs text-muted/60 border-l border-border-dim pl-3">
          MassTransit · RabbitMQ · .NET 8
        </span>
      </div>
      <div
        className="flex items-center gap-2"
        aria-label={apiOnline ? 'API is connected' : 'API is offline'}
        role="status"
      >
        {apiOnline ? (
          <>
            <span className="h-2 w-2 rounded-full bg-accent animate-pulse-dot" aria-hidden />
            <span className="text-xs text-muted">API Connected</span>
          </>
        ) : (
          <>
            <WifiOff className="h-3.5 w-3.5 text-danger" aria-hidden />
            <span className="text-xs text-danger">API Offline</span>
          </>
        )}
      </div>
    </header>
  )
}

function OfflineBanner() {
  return (
    <div
      role="alert"
      className="mx-4 mt-3 flex items-center gap-3 rounded-md border border-danger/30 bg-danger/10 px-4 py-3"
    >
      <WifiOff className="h-4 w-4 text-danger flex-shrink-0" aria-hidden />
      <div>
        <p className="text-sm font-medium text-danger">API Unreachable</p>
        <p className="text-xs text-danger/70 mt-0.5">
          Start the backend: <code className="font-mono">dotnet run --project src/BankingMessaging.Api</code>
        </p>
      </div>
    </div>
  )
}

export default function App() {
  const [prefilledFrom, setPrefilledFrom] = useState<string | undefined>()

  const { data: accounts, isLoading: accountsLoading } = useAccounts()
  const { data: transfers, isLoading: transfersLoading } = useTransfers()
  const { isSuccess: apiOnline } = useApiHealth()

  const safeAccounts = accounts ?? []
  const safeTransfers = transfers ?? []

  return (
    <>
      <Toaster
        position="top-right"
        toastOptions={{
          style: {
            background: '#1a1d27',
            border: '1px solid #2a2d3a',
            color: '#e8eaf0',
            fontFamily: 'Outfit, sans-serif',
            fontSize: '13px',
          },
        }}
      />

      <div className="min-h-screen flex flex-col bg-base">
        <Header apiOnline={apiOnline} />

        {!apiOnline && !accountsLoading && <OfflineBanner />}

        <main
          className={cn(
            'flex-1 p-4 gap-4',
            'grid grid-cols-1',
            'lg:grid-cols-[280px_1fr_300px]',
          )}
          style={{ minHeight: 0 }}
        >
          {/* ── Left sidebar: Accounts + Chaos Controls ─────────────────────── */}
          <aside className="flex flex-col gap-4 overflow-y-auto">
            <div className="space-y-1">
              <p className="text-[10px] text-muted/50 uppercase tracking-widest font-medium px-0.5">
                Accounts
              </p>
            </div>

            {accountsLoading ? (
              <>
                <AccountCardSkeleton />
                <AccountCardSkeleton />
              </>
            ) : safeAccounts.length === 0 ? (
              <div className="rounded-md border border-border-dim bg-surface p-4 text-center">
                <p className="text-xs text-muted/60">No accounts found.</p>
                <p className="text-[10px] text-muted/40 mt-1">
                  Run EF migrations to seed data.
                </p>
              </div>
            ) : (
              safeAccounts.map(account => (
                <AccountCard
                  key={account.accountId}
                  account={account}
                  isSelected={prefilledFrom === account.accountId}
                  onSelect={id => setPrefilledFrom(id)}
                />
              ))
            )}

            {safeAccounts.length > 0 && (
              <RetrySimulator accounts={safeAccounts} />
            )}
          </aside>

          {/* ── Main: Transfer form + history ────────────────────────────────── */}
          <div className="flex flex-col gap-4 min-h-0">
            <TransferForm
              accounts={safeAccounts}
              loading={accountsLoading}
              prefilledFrom={prefilledFrom}
              onPrefilledConsumed={() => setPrefilledFrom(undefined)}
            />
            <TransferList
              transfers={safeTransfers}
              loading={transfersLoading}
            />
          </div>

          {/* ── Right rail: Activity feed ─────────────────────────────────────── */}
          <aside className="min-h-0">
            <ActivityFeed
              transfers={safeTransfers}
              loading={transfersLoading}
            />
          </aside>
        </main>
      </div>
    </>
  )
}
