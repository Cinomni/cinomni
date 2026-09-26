import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Every test renders into a fresh document: a leaked tree makes `getByRole` ambiguous in the next one.
afterEach(cleanup)
