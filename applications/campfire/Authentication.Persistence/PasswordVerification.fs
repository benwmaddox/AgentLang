namespace AgentLang.Campfire.Authentication.Persistence

open System

module internal PasswordVerification =
    [<Literal>]
    let DummyDigest = "$2a$12$FiKmSp4UhLvSB4Sd/ZUjQunyKP6.NjDRHdr5LnKUVk.BUn4Mq12WS"

    /// Delegate bcrypt format and variant handling to the established library.
    /// Invalid stored hashes reject credentials; unexpected provider failures
    /// propagate to the persistence boundary as structured Unavailable errors.
    let verify (password: string) (digest: string) =
        if isNull password || isNull digest then false
        else
            try BCrypt.Net.BCrypt.Verify(password, digest)
            with
            | :? BCrypt.Net.SaltParseException -> false
            | :? ArgumentException -> false
