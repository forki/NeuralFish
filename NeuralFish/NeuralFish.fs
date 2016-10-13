module NeuralFish

//value*weight
type Synapse = int*float*float

let synapseDotProduct synapses =
  let rec loop synapses =
    match synapses with
    | [] -> 0.0
    | (_,value,weight)::tail -> value*weight + (loop tail)
  synapses |> Seq.toList |> loop

type NeuronActions =
  | Sync
  | ReceiveInput of Synapse
  | IncrementBarrierThreshold of AsyncReplyChannel<unit>

type NeuronInstance = MailboxProcessor<NeuronActions>

type NeuronConnection =
  {
    neuron: NeuronInstance
    weight: float
  }

type NeuronProperties =
  {
    id: int
    bias: float
    activationFunction: float -> float
    outbound_connections: NeuronConnection seq
  }

type SensorProperties =
  {
    id: int
    syncFunction: unit -> float seq
    outbound_connections:  NeuronConnection seq
  }

type ActuatorProperties =
  {
    id: int
    outputHook: float -> unit
  }

type NeuronType =
  | Neuron of NeuronProperties
  | Sensor of SensorProperties
  | Actuator of ActuatorProperties

type NeuronIdGeneratorMsg =
  | GetNeuronId of AsyncReplyChannel<int>

let private neuronIdGenerator =
  let generator = MailboxProcessor<NeuronIdGeneratorMsg>.Start(fun inbox ->
    let rec loop currentNumber =
      async {
        let! msg = inbox.Receive ()
        match msg with
        | GetNeuronId replyChannel ->
          currentNumber |> replyChannel.Reply
          return! loop (currentNumber+1)
      }
    loop 0
  )
  (fun () -> GetNeuronId |> generator.PostAndReply)

let createNeuron activationFunction bias =
  {
    id = neuronIdGenerator()
    bias = bias
    activationFunction = activationFunction
    outbound_connections = Seq.empty
  } |> Neuron
let createSensor syncFunction =
  {
    id = neuronIdGenerator()
    syncFunction = syncFunction
    outbound_connections = Seq.empty
  } |> Sensor
let createActuator outputHook =
  {
    id = neuronIdGenerator()
    outputHook = outputHook
  } |> Actuator

let private addNodeToConnections weight toNode outbound_connections =
  let addConnection connectionSet connection =
    IncrementBarrierThreshold |> connection.neuron.PostAndReply
    connectionSet |> Seq.append (Seq.singleton connection)
  let neuronConnection =
    {
      weight = weight
      neuron = toNode
    }
  neuronConnection |> addConnection outbound_connections

let connectNodeToNeuron weight toNode fromNode  =

  match fromNode with
    | Neuron props ->
      let newOutboundConnections = addNodeToConnections weight toNode props.outbound_connections
      Neuron <| { props with outbound_connections = newOutboundConnections  }
    | Sensor props ->
      let newOutboundConnections = addNodeToConnections weight toNode props.outbound_connections
      Sensor <| { props with outbound_connections = newOutboundConnections }
    | Actuator props -> fromNode

let connectSensorToNode weights toNode fromNode =
  match fromNode with
    | Sensor props ->
      let rec getConnectionsFromWeights weights connections =
        if (weights |> Seq.isEmpty) then
          connections
        else
          let weight = weights |> Seq.head
          let weights = weights |> Seq.tail
          let newOutboundConnections = addNodeToConnections weight toNode connections
          getConnectionsFromWeights weights newOutboundConnections
      let newOutboundConnections = getConnectionsFromWeights weights props.outbound_connections
      Sensor <| { props with outbound_connections = newOutboundConnections }
    | _ -> connectNodeToNeuron (weights |> Seq.head) toNode fromNode

let connectNodeToActuator toNode fromNode =
  fromNode |> connectNodeToNeuron 0.0 toNode

let createNeuronInstance neuronType =
  let isBarrierSatisifed barrierThreshold barrier =
    barrierThreshold = (barrier |> Seq.length)
  let sendSynapseToNeurons (outputNeurons : NeuronConnection seq ) partialSynapse =
    let sendSynapseToNeuron (nodeId, outputValue) outputNeuronConnection =
      (nodeId, outputValue, outputNeuronConnection.weight)
      |> ReceiveInput
      |> outputNeuronConnection.neuron.Post
    outputNeurons
    |> Seq.iter (sendSynapseToNeuron partialSynapse)
  let addBias bias outputVal =
    outputVal + bias
  let activateNeuron barrier neuronType =
    match neuronType with
    | Neuron props ->
        barrier
        |> synapseDotProduct
        |> addBias props.bias
        |> (fun outputValue -> (props.id, (props.activationFunction outputValue)))
        |> sendSynapseToNeurons props.outbound_connections
        Seq.empty
    | Actuator props ->
        barrier
        |> Seq.sumBy(fun (_,value,weight) -> value)
        |> props.outputHook
        Seq.empty
    | Sensor _ ->
        barrier

  NeuronInstance.Start(fun inbox ->
    let rec loop barrier barrierThreshold =
      async {
        let! msg = inbox.Receive ()
        match msg with
        | Sync ->
          match neuronType with
          | Neuron _ ->
            return! loop barrier barrierThreshold
          | Actuator _ ->
            return! loop barrier barrierThreshold
          | Sensor props ->
            let rec processSensorSync connections dataStream =
              let sendSynapseToNeuron outboundNeuronConnection (nodeId, outputValue)  =
                printfn "Output %A to node %A with weight %A" outputValue nodeId outboundNeuronConnection.weight
                (nodeId, outputValue, outboundNeuronConnection.weight)
                |> ReceiveInput
                |> outboundNeuronConnection.neuron.Post
              if (connections |> Seq.isEmpty) then
                ()
              else
                let data = dataStream |> Seq.head
                let dataStream = (dataStream |> Seq.tail) |> Seq.append(Seq.singleton data)
                let connection = connections |> Seq.head
                let tailConnections = connections |> Seq.tail
                (props.id, data) |> sendSynapseToNeuron connection
                processSensorSync tailConnections dataStream
            props.syncFunction()
            |> processSensorSync props.outbound_connections
            return! loop barrier barrierThreshold
        | ReceiveInput package ->
          match neuronType with
          | Neuron props ->
            let barrier =
              barrier
              |> Seq.append (Seq.singleton package)
            if barrier |> isBarrierSatisifed barrierThreshold then
              let barrier = neuronType |> activateNeuron barrier
              return! loop barrier barrierThreshold
            else
              return! loop barrier barrierThreshold
          | Actuator props ->
            let barrier =
             barrier
             |> Seq.append (Seq.singleton package)
            if barrier |> isBarrierSatisifed barrierThreshold then
              let barrier = neuronType |> activateNeuron barrier
              return! loop Seq.empty barrierThreshold
            else
              return! loop barrier barrierThreshold
          | Sensor _ ->
            //Sensors use the sync msg
            return! loop barrier barrierThreshold
        | IncrementBarrierThreshold replyChannel ->
          replyChannel.Reply()
          let barrierThreshold = barrierThreshold + 1
          return! loop barrier barrierThreshold
      }
    loop Seq.empty 0
  )
