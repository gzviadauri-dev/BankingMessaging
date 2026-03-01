import { CheckCircle2, Clock, XCircle, ArrowDownUp } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import type { TransferStatus } from '@/types'

interface TransferStatusBadgeProps {
  status: TransferStatus
}

export function TransferStatusBadge({ status }: TransferStatusBadgeProps) {
  switch (status) {
    case 'Pending':
      return (
        <Badge variant="pending" aria-label="Status: Pending">
          <span className="h-1.5 w-1.5 rounded-full bg-warning animate-pulse-dot" aria-hidden />
          Pending
        </Badge>
      )
    case 'Debited':
      return (
        <Badge variant="debited" aria-label="Status: Debited">
          <ArrowDownUp className="h-3 w-3" aria-hidden />
          Debited
        </Badge>
      )
    case 'Completed':
      return (
        <Badge variant="completed" aria-label="Status: Completed">
          <CheckCircle2 className="h-3 w-3" aria-hidden />
          Completed
        </Badge>
      )
    case 'Failed':
      return (
        <Badge variant="failed" aria-label="Status: Failed">
          <XCircle className="h-3 w-3" aria-hidden />
          Failed
        </Badge>
      )
    default:
      return (
        <Badge variant="muted" aria-label={`Status: ${status}`}>
          <Clock className="h-3 w-3" aria-hidden />
          {status}
        </Badge>
      )
  }
}
