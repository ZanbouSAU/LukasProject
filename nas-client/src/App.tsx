// src/App.tsx

import { AuthProvider, useAuth } from './auth/AuthContext'
import { FileBrowser } from './components/FileBrowser'
import { LoginPage } from './components/LoginPage'
import { Spinner } from './components/ui'

function Gate() {
  const { ready, email } = useAuth()
  if (!ready) {
    return (
      <div className="flex min-h-full items-center justify-center">
        <Spinner className="size-6" />
      </div>
    )
  }
  return email ? <FileBrowser /> : <LoginPage />
}

export default function App() {
  return (
    <AuthProvider>
      <Gate />
    </AuthProvider>
  )
}
