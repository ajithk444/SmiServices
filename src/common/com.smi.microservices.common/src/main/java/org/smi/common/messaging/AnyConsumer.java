
package org.smi.common.messaging;

import java.io.IOException;

import org.smi.common.messages.MessageHeader;

import com.rabbitmq.client.AMQP.BasicProperties;
import com.rabbitmq.client.Channel;
import com.rabbitmq.client.Envelope;

/**
 * Helper class for creating a consumer for any message type, useful for
 * testing.
 * 
 * Usage:
 * 
 * Create the consumer:
 * 
 * AnyConsumer<MyMessage> myConsumer = new
 * AnyConsumer<MyMessage>(MyMessage.class);
 * 
 * Then set up as per usual using the RabbitMQ adapter:
 * 
 * myRabbitMQAdapter.StartConsumer(myLabel, myConsumer);
 * 
 * Then wait and see if we've received a valid message:
 *
 * while (!myMessage.isMessageValid()) {}
 * 
 * (note, probably should put a time limit on this otherwise infinite loop!)
 * 
 * then when loop breaks, you can get the message via:
 * 
 * MyMessage recv = myConsumer.getMessage();
 * 
 * @param <T>
 *            The type of the message we want to consume
 */
public class AnyConsumer<T> extends SmiConsumer {

    final Class<T> _typeParameterClass;

    private T _message;
    private volatile boolean _messageValid;

    public AnyConsumer(Class<T> typeParameterClass,Channel chan) {
        super(chan);
        this._typeParameterClass = typeParameterClass;
        _message = null;
        _messageValid = false;
    }

    @Override
    public void handleDeliveryImpl(String consumerTag, Envelope envelope, BasicProperties properties, byte[] body,
            MessageHeader header) throws IOException {

        try {

            _message = getMessageFromBytes(body, _typeParameterClass);

        } catch (IOException e) {

            NackMessage(envelope.getDeliveryTag());
            throw e;
        }

        AckMessage(envelope.getDeliveryTag());
        _messageValid = true;
    }

    /**
     * @return True if a valid message has been received
     */
    public boolean isMessageValid() {
        return _messageValid;
    }

    /**
     * Gets the received message and resets the consumer
     * 
     * @return The received message
     */
    public T getMessage() {

        T message = null;

        if (isMessageValid()) {

            message = _message;

            // Reset consumer
            _message = null;
            _messageValid = false;
        }

        return message;
    }
}
