export interface Account {
  accountId: string
  ownerId: string
  balance: number
  currency: string
  updatedAt: string
}

export type TransferStatus = 'Pending' | 'Debited' | 'Completed' | 'Failed'

export interface Transfer {
  transferId: string
  correlationId?: string
  fromAccountId: string
  toAccountId: string
  amount: number
  currency: string
  status: TransferStatus
  failureReason?: string | null
  createdAt: string
  completedAt?: string | null
}

export interface InitiateTransferRequest {
  fromAccountId: string
  toAccountId: string
  amount: number
  currency: string
  simulateError?: boolean
}

export interface TransferResponse {
  transferId: string
  correlationId: string
}
