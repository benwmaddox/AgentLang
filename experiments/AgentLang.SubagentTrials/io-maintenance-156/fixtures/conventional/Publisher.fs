namespace AgentLang.SubagentTrials.IoMaintenance156.Fixture

module Configuration =
    /// Copy the exact source contents to the destination.
    let publish (files: VirtualFiles, source: string, destination: string) : unit =
        let contents = files.Read(source)
        files.Write(destination, contents)

    /// Keep the original unconditional refresh behavior through the shared publisher.
    let refresh (files: VirtualFiles, source: string, destination: string) : unit =
        publish(files, source, destination)
